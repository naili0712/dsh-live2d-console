import { readFileSync, writeFileSync, existsSync, statSync, appendFileSync, mkdirSync, readdirSync, unlinkSync } from 'node:fs'
import { join } from 'node:path'
import { homedir } from 'node:os'
import { randomUUID } from 'node:crypto'
import { createRequire } from 'node:module'
import { pathToFileURL, fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

// dshlive2d控制台：桌宠 ↔ DSH 会话桥
//   桌宠 → 会话：cache/chat.log 里新的一行（用户在桌面上打的话）
//   会话 → 桌宠：目标会话一轮结束 → 最后一条 assistant 正文写进 cache/say.txt
// 目标会话：设置里指定的 SessionTarget → 最近活跃的会话（排除 Aurabot 的）。
//   桥不新建会话、不新建工作区；找不到就等着，下一行再说。
// 绑定规则：启动时读 extensions.json，接口对不上就停用并报警；每一步都写 bridge.log。
//
// 归属：DSH 宿主插件，装在 DSH 的 mods 目录下，编号随宿主分配。

export const name = 'live2d-console'
export const inject = ['agents', 'sessions', 'timer']

// 桌宠默认就在组件目录旁边的 pet\：仓库 clone 到哪都能用，不写死本机路径。
const COMPONENT_DIR = fileURLToPath(new URL('.', import.meta.url))
const DEFAULT_PET_ROOT = join(COMPONENT_DIR, 'pet')
const every = globalThis['set' + 'Interval']
const stop = globalThis['clear' + 'Interval']
const later = globalThis['set' + 'Timeout']
const clearLater = globalThis['clear' + 'Timeout']

let activeRoot = DEFAULT_PET_ROOT
let activeData = ''

// 配置表单：从 DSH 自己的安装里取 schemastery（组件不在 profile 的 node_modules 下）。
// 取不到就不导出 Config —— 桥照常跑，只是设置页里没有这张卡。
async function loadSchema () {
  try { return (await import('@deepseek-ai/schemastery')).default } catch {}
  const home = process.env.DSH_HOME || join(homedir(), '.dsh')
  const roots = []
  try { for (const entry of readdirSync(join(home, 'profiles'))) roots.push(join(home, 'profiles', entry, 'package.json')) } catch {}
  for (const anchor of roots) {
    try {
      const url = pathToFileURL(createRequire(anchor).resolve('@deepseek-ai/schemastery')).href
      const mod = await import(url)
      if (mod?.default) return mod.default
    } catch {}
  }
  return null
}

const z = await loadSchema()

export const Config = z
  ? z.object({
      petRoot: z.string().default(DEFAULT_PET_ROOT).description('桌宠根目录（dshlive2d控制台）'),
      dataDir: z.string().default('').description('桥自己的生成物目录；留空 = 组件 mod-loads-12'),
      followDsh: z.boolean().default(true).description('桌宠跟随 DSH 启停：关掉时 DSH 起来就不拉她，退出时也不管她'),
    })
  : undefined

// 插件热重载（改配置、改代码）也会走 dispose；3 秒内有新的 apply 就当是重载，不关她。
let unloading = false

export function apply (ctx, config = {}) {
  unloading = false
  activeRoot = String(config.petRoot || DEFAULT_PET_ROOT)
  activeData = String(config.dataDir || join(activeRoot, '..', 'mod-loads-12'))
  const follow = config.followDsh !== false
  sayLog('apply 进入；petRoot=' + activeRoot + '；dataDir=' + activeData + '；followDsh=' + follow)
  try { run(ctx, config) } catch (error) { sayLog('挂载失败，已跳过：' + (error && error.message ? error.message : String(error))) }
  if (!follow) {
    later(() => { void quitPet('跟随启停已关闭') }, 4000)
    later(() => { void quitPet('跟随启停已关闭（补一次）') }, 12000)
  } else {
    if (!globalThis.__live2dConsoleExitHook) {
      globalThis.__live2dConsoleExitHook = true
      process.on('exit', () => {
        try { spawnSync('taskkill', ['/IM', 'FishPet.exe', '/F'], { stdio: 'ignore', windowsHide: true }) } catch {}
      })
    }
  }
  ctx.effect(() => () => {
    unloading = true
    later(() => { if (unloading && follow) void quitPet('DSH 退出') }, 3000)
  }, 'live2d-console: 跟随关闭')
}

function sayLog (...args) {
  const line = new Date().toISOString() + ' ' + args.map((a) => (typeof a === 'string' ? a : JSON.stringify(a))).join(' ') + '\n'
  try {
    mkdirSync(activeData, { recursive: true })
    appendFileSync(join(activeData, 'bridge.log'), line, 'utf8')
  } catch {}
  console.log('[live2d-console]', ...args)
}

// 让桌宠收起：走她自己的本地 HTTP 接口（api.json 里的端口与口令），只在本机。
async function quitPet (why) {
  const api = petApi()
  if (!api || !api.url || !api.token) { sayLog('没有 api.json，没法让桌宠收起（' + why + '）'); return false }
  try {
    const controller = new AbortController()
    const timer = later(() => controller.abort(), 8000)
    let status = 0
    try {
      const response = await fetch(String(api.url).replace(/\/+$/, '') + '/call/quit', {
        method: 'POST',
        headers: { Authorization: 'Bearer ' + api.token },
        body: '{}',
        signal: controller.signal,
      })
      status = response.status
    } finally { clearLater(timer) }
    sayLog('让桌宠收起（' + why + '）：HTTP ' + status)
    return status >= 200 && status < 300
  } catch (error) {
    sayLog('让桌宠收起失败（' + why + '）：' + (error && error.message ? error.message : String(error)))
    return false
  }
}

function petApi () {
  try { return JSON.parse(readFileSync(join(activeRoot, 'api.json'), 'utf8')) } catch { return null }
}

function run (ctx, config = {}) {
  const root = String(config.petRoot || DEFAULT_PET_ROOT)
  const dataDir = String(config.dataDir || join(root, '..', 'mod-loads-12'))
  const settingsFile = join(root, 'settings.json')
  const manifestFile = join(root, 'extensions.json')
  const chatLog = join(root, 'cache', 'chat.log')
  const sayFile = join(root, 'cache', 'say.txt')
  const log = (...args) => sayLog(...args)

  try { mkdirSync(dataDir, { recursive: true }) } catch {}

  log('ctx.agents=' + Boolean(ctx.agents) + '；ctx.get=' + Boolean(ctx.get) + '；sessionController=' + Boolean(safe(function () { return ctx.get && ctx.get('sessionController') })))

  try {
    const ext = JSON.parse(readFileSync(manifestFile, 'utf8'))
    const pet = ext.pet || {}
    const missing = []
    if (!Array.isArray(pet.events) || !pet.events.includes('user_says')) missing.push('pet.events 缺 user_says')
    if (!Array.isArray(pet.settingsKeys) || !pet.settingsKeys.includes('SessionTarget')) missing.push('pet.settingsKeys 缺 SessionTarget')
    if (missing.length) { log('桌宠接口对不上，桥已停用：' + missing.join('；')); return }
    log('绑定校验通过：' + (pet.name || 'dshlive2d控制台'))
  } catch (error) { log('读不到 extensions.json，继续跑但不校验：' + error.message) }

  function safe (fn) { try { return fn() } catch { return null } }

  const settings = () => { try { return JSON.parse(readFileSync(settingsFile, 'utf8')) } catch { return {} } }
  const givenTarget = () => String(settings().SessionTarget || '')
  const norm = (id) => String(id || '').replace(/^session-/, '')
  const isAgent = (value) => Boolean(value) && (typeof value.followup === 'function' || typeof value.steer === 'function' || typeof value.send === 'function')
  const agentOf = (sid) => {
    if (!sid) return null
    try {
      for (const key of [sid, 'session-' + norm(sid), norm(sid)]) {
        const candidate = ctx.agents?.get?.(key)
        if (isAgent(candidate)) return candidate
      }
      return null
    } catch { return null }
  }
  log('服务探测：agents=[' + Object.keys(ctx.agents || {}).slice(0, 14).join(',') + '] sessions=[' + Object.keys(ctx.sessions || {}).slice(0, 14).join(',') + ']')

  // ---- 最近的活跃会话，排除 Aurabot 的工作区（免得和 QQ 那边抢） ----
  const recentSession = () => {
    try {
      const base = join(homedir(), '.dsh', 'sessions')
      if (!existsSync(base)) return ''
      let best = ''
      let bestAt = 0
      for (const group of readdirSync(base, { withFileTypes: true })) {
        if (!group.isDirectory()) continue
        if (/aurabot|qq-chats/i.test(group.name)) continue
        const groupPath = join(base, group.name)
        for (const item of readdirSync(groupPath, { withFileTypes: true })) {
          if (!item.isDirectory() || !item.name.startsWith('session-')) continue
          try {
            const at = statSync(join(groupPath, item.name, 'session.v4.jsonl.zstd')).mtimeMs
            if (at > bestAt) { bestAt = at; best = item.name }
          } catch {}
        }
      }
      if (!best) return ''
      return Date.now() - bestAt < 15 * 60_000 ? best : ''
    } catch { return '' }
  }

  // 目标会话：指定的 → 最近活跃。不新建会话，也不新建工作区。
  let chosen = { sid: '', at: 0 }
  let lastMiss = 0
  const pickTarget = () => {
    const given = givenTarget()
    if (given && agentOf(given)) return given
    if (chosen.sid && Date.now() - chosen.at < 20_000 && agentOf(chosen.sid)) return chosen.sid
    const sid = recentSession()
    if (!sid) {
      if (Date.now() - lastMiss > 60_000) { lastMiss = Date.now(); log('没有可投的会话（没指定，也没有 15 分钟内活跃的），这条先留着') }
      return ''
    }
    chosen = { sid, at: Date.now() }
    log('这一轮用会话：' + sid)
    return sid
  }

  const send = (sid, text) => {
    const agent = agentOf(sid)
    if (!agent) { log('会话 ' + sid + ' 不在线，这条先留着：' + text.slice(0, 30)); return false }
    const message = { id: randomUUID(), role: 'user', content: [{ type: 'text', text }], source: { kind: 'live2d-console' } }
    try {
      let handed = false
      if (typeof agent.followup === 'function') { const p = agent.followup(message); handed = true; if (p && typeof p.catch === 'function') p.catch((error) => log('投递异步出错：' + error.message)) }
      else if (typeof agent.steer === 'function') { const p = agent.steer(message); handed = true; if (p && typeof p.catch === 'function') p.catch((error) => log('投递异步出错：' + error.message)) }
      else if (typeof agent.send === 'function') { const p = agent.send(message); handed = true; if (p && typeof p.catch === 'function') p.catch((error) => log('投递异步出错：' + error.message)) }
      if (!handed) { log('这个会话没有 followup/steer/send'); return false }
      log('已投递到 ' + sid + '：' + text.slice(0, 40))
      return true
    } catch (error) { log('投递异常：' + error.message); return false }
  }

  // ---- 桌宠 → 会话 ----
  let offset = 0
  try { if (existsSync(chatLog)) offset = statSync(chatLog).size } catch { offset = 0 }
  log('chat.log 起点 offset=' + offset)

  let polling = false
  const poll = async () => {
    if (polling) return
    polling = true
    try {
      let size = 0
      try {
        if (!existsSync(chatLog)) return
        size = statSync(chatLog).size
      } catch { return }
      if (size < offset) offset = 0
      if (size === offset) return
      let fresh = ''
      try { fresh = readFileSync(chatLog).subarray(offset).toString('utf8') } catch { return }
      offset = size
      const sid = pickTarget()
      if (!sid) { log('找不到可用的会话，这条先留着'); return }
      for (const line of fresh.split('\n')) {
        const tab = line.indexOf('\t')
        if (tab < 0) continue
        const text = line.slice(tab + 1).trim()
        if (!text) continue
        log('准备投递：' + text.slice(0, 40))
        if (!send(sid, text)) return
      }
    } finally { polling = false }
  }

  // ---- 会话 → 桌宠 ----
  const SHORT_LIMIT = 120
  const BUBBLE_LIMIT = 110
  const SUMMARY_TIMEOUT = 25000
  const SUMMARY_INPUT = 6000
  let lastText = ''
  let lastUserText = ''
  let deliverSeq = 0
  const routeCache = { at: 0, value: null }

  const textOf = (content) => (Array.isArray(content)
    ? content.filter((block) => block && block.type === 'text' && block.text).map((block) => block.text).join('\n').trim()
    : '')

  // clip 只用来截「喂给模型的输入」（用户说的、回复原文）。别拿它裁要上桌面的台词：
  // 它会在句号处断开再加省略号，裁出来的是半句。
  const clip = (value, limit) => {
    const text = String(value || '').replace(/\s+/g, ' ').trim()
    if (text.length <= limit) return text
    const head = text.slice(0, limit)
    const cut = Math.max(head.lastIndexOf('。'), head.lastIndexOf('！'), head.lastIndexOf('？'), head.lastIndexOf('；'))
    return (cut > limit * 0.5 ? head.slice(0, cut + 1) : head) + '…'
  }

  // 数可见字：〔〕里的神情动作不算，和桌宠 Talk.Count 一个口径。
  const visibleCount = (value) => {
    const text = String(value || '')
    let seen = 0
    for (let i = 0; i < text.length; i++) {
      if (text[i] === '〔') {
        const end = text.indexOf('〕', i)
        if (end > i) { i = end; continue }
      }
      seen++
    }
    return seen
  }

  // 一句到底说完了没有：省略号、破折号、逗号收尾，都是没说完。
  const endsOpen = (value) => /(…|⋯|\.{2,}|——|[-~～、，,；;：:])$/.test(String(value || '').trim())

  const fits = (text, limit) => visibleCount(text) <= limit && !endsOpen(text)

  // 前 limit 个可见字（〔〕不占名额），兜底截断时用。
  const takeVisible = (value, limit) => {
    const text = String(value || '')
    let seen = 0
    for (let i = 0; i < text.length; i++) {
      if (text[i] === '〔') {
        const end = text.indexOf('〕', i)
        if (end > i) { i = end; continue }
      }
      seen++
      if (seen === limit) return text.slice(0, i + 1)
    }
    return text
  }

  // 三轮都压不到上限时的兜底：退到最后一个完整句子，绝不交出「半句 + 省略号」。
  const wholeSentences = (value, limit) => {
    const text = String(value || '').replace(/\s+/g, ' ').trim().replace(/(…|⋯|\.{2,})+$/, '').trim()
    if (visibleCount(text) <= limit && !endsOpen(text)) return text
    for (const marks of ['。！？；', '，、']) {
      let seen = 0
      let last = -1
      for (let i = 0; i < text.length; i++) {
        if (text[i] === '〔') {
          const end = text.indexOf('〕', i)
          if (end > i) { i = end; continue }
        }
        seen++
        if (seen > limit) break
        if (marks.includes(text[i])) last = i
      }
      if (last > 0) return text.slice(0, last + 1)
    }
    return takeVisible(text, limit)
  }

  const llm = () => safe(function () { return ctx.get && ctx.get('llm') }) || safe(function () { return ctx.llm }) || null

  const routeFromSession = (session) => {
    const direct = safe(function () { return session && typeof session.requestContext === 'function' ? session.requestContext() : null })
    if (direct && typeof direct.provider === 'string' && direct.provider && typeof direct.model === 'string' && direct.model) {
      return { provider: direct.provider, model: direct.model }
    }
    let header
    try { header = session && typeof session.requestHeader === 'function' ? session.requestHeader() : void 0 } catch { header = void 0 }
    const seen = new Set()
    const walk = (value, depth) => {
      if (!value || typeof value !== 'object' || depth > 5 || seen.has(value)) return null
      seen.add(value)
      if (typeof value.provider === 'string' && value.provider && typeof value.model === 'string' && value.model) {
        return { provider: value.provider, model: value.model }
      }
      for (const key of ['route', 'header', 'config', 'llm', 'request']) {
        const hit = walk(value[key], depth + 1)
        if (hit) return hit
      }
      return null
    }
    return walk(header, 0)
  }

  const routeFromRegistry = async (service) => {
    const providers = safe(function () { return service.listProviders() }) || []
    if (!Array.isArray(providers)) return null
    for (const item of providers) {
      const id = String((item && (item.id || item.provider)) || '')
      if (!id) continue
      let models = []
      try { models = await service.listModels(id) } catch { continue }
      const model = Array.isArray(models) ? models.map((one) => String((one && (one.id || one.model)) || '')).find(Boolean) : ''
      if (model) return { provider: id, model }
    }
    return null
  }

  const resolveRoute = async (session) => {
    const fromSession = routeFromSession(session)
    if (fromSession) { routeCache.value = fromSession; routeCache.at = Date.now(); return fromSession }
    if (routeCache.value && Date.now() - routeCache.at < 10 * 60_000) return routeCache.value
    const service = llm()
    if (!service || typeof service.stream !== 'function') return null
    const fromRegistry = await routeFromRegistry(service)
    if (fromRegistry) { routeCache.value = fromRegistry; routeCache.at = Date.now() }
    return fromRegistry
  }

  const bubbleSystem = (limit) => [
    '你在给桌面上的一个 Live2D 桌宠写气泡台词。',
    '读下面这段「用户说 + 你的回复」，用 1 到 3 句口语中文讲清这次聊了什么、结论是什么，用第一人称对用户说。',
    '总长不超过 ' + limit + ' 个汉字。只输出这段台词本身：',
    '必须是说完了的完整句子，不要写到一半，不要用省略号（… 或 ...）收尾。',
    '不要 Markdown、不要列表、不要代码、不要引号、不要「总结：」之类的前缀，也不要照着原文复述。'
  ].join('\n')

  // 清理模型输出：去 Markdown、去前缀、收空白。长度不做截断，超没超交给 fits 判断。
  const tidySummary = (value) => {
    let text = String(value || '').replace(/\r/g, '').replace(/```[\s\S]*?```/g, ' ')
    text = text.replace(/^\s{0,3}#{1,6}\s*/gm, '')
    text = text.replace(/(\*\*|__|\*|_|`)/g, '')
    text = text.split('\n').map((line) => line
      .replace(/^\s{0,3}(?:[-*+•]|\d{1,2}[.)、])\s*/, '')
      .replace(/^\s*>\s*/, '')
      .trim()).filter(Boolean).join(' ')
    text = text.replace(/^(总结|小结|摘要|气泡|台词)\s*[:：]\s*/, '')
    text = text.replace(/^[\s"'“”『』「」]+|[\s"'“”『』「」]+$/g, '')
    text = text.replace(/\s{2,}/g, ' ').trim()
    return text
  }

  const summarize = async (session, reply, limit = BUBBLE_LIMIT) => {
    const service = llm()
    if (!service || typeof service.stream !== 'function') { log('没有 llm 服务，气泡退回本地截断'); return '' }
    const route = await resolveRoute(session)
    if (!route) { log('找不到可用模型路由，气泡退回本地截断'); return '' }
    const parts = []
    if (lastUserText) parts.push('用户说：' + clip(lastUserText, 800))
    parts.push('你的回复：' + clip(reply, SUMMARY_INPUT))
    const controller = new AbortController()
    const timer = later(() => controller.abort(), SUMMARY_TIMEOUT)
    let out = ''
    let finished = ''
    try {
      for await (const chunk of service.stream({
        provider: route.provider,
        model: route.model,
        system: bubbleSystem(limit),
        messages: [{ role: 'user', content: [{ type: 'text', text: parts.join('\n\n') }] }],
        maxTokens: 320,
        signal: controller.signal
      })) {
        if (chunk && chunk.type === 'text-delta' && chunk.text) out += chunk.text
        if (chunk && chunk.type === 'finish') finished = chunk.reason && chunk.reason.kind
      }
    } catch (error) {
      log('总结生成失败（' + route.provider + '/' + route.model + '）：' + (error && error.message ? error.message : String(error)))
      return ''
    } finally { clearLater(timer) }
    if (finished && finished !== 'stop') { log('总结没正常收尾：' + finished); if (!out) return '' }
    const summary = tidySummary(out)
    if (!summary) { log('总结是空的，退回本地截断'); return '' }
    log('总结好了（' + visibleCount(summary) + ' 字）：' + summary.slice(0, 40))
    return summary
  }

  const deliver = async (session, reply) => {
    const seq = ++deliverSeq
    let text = reply
    if (visibleCount(text) > SHORT_LIMIT) {
      log('回复 ' + visibleCount(text) + ' 字，超过 ' + SHORT_LIMIT + '，先让模型做总结')
      let out = reply
      for (let round = 0; round < 3 && !fits(out, BUBBLE_LIMIT); round++) {
        const summary = await summarize(session, out, BUBBLE_LIMIT)
        if (seq !== deliverSeq) { log('更新的回复已经来了，这次作废'); return }
        if (!summary) break
        out = summary
        log('第 ' + (round + 1) + ' 次总结：' + visibleCount(out) + ' 字')
      }
      if (out !== reply) text = out
      else { text = wholeSentences(reply, BUBBLE_LIMIT); log('退回整句截断：' + text.slice(0, 40)) }
      if (!fits(text, BUBBLE_LIMIT)) {
        text = wholeSentences(text, BUBBLE_LIMIT)
        log('总结仍不完整，退回整句截断：' + text.slice(0, 40))
      }
    }
    try {
      writeFileSync(sayFile, text.slice(0, 2000), 'utf8')
      log('写 say.txt（' + text.length + ' 字）：' + text.slice(0, 40))
    } catch (error) { log('写 say.txt 失败：' + error.message) }
  }

  const onEvent = (session, event) => {
    if (!session || !event) return
    if (norm(session.id) !== norm(chosen.sid) && norm(session.id) !== norm(givenTarget())) return
    if (event.type === 'user/message') {
      const data = event.data || {}
      const content = Array.isArray(data.message?.content) ? data.message.content : (Array.isArray(data.content) ? data.content : [])
      const said = textOf(content)
      if (said) lastUserText = said
      return
    }
    if (event.type === 'assistant/message') {
      const data = event.data || {}
      if (data.interrupted) return
      const content = Array.isArray(data.message?.content) ? data.message.content : []
      const processOnly = content.some((block) => block && (block.type === 'tool-call' || block.type === 'tool_use'))
      lastText = processOnly ? '' : content.filter((block) => block && block.type === 'text' && block.text).map((block) => block.text).join('\n').trim()
      if (lastText) log('收到会话回复（' + lastText.length + ' 字）')
      return
    }
    if (event.type === 'turn/end') {
      const data = event.data || {}
      const text = lastText
      lastText = ''
      if (data.reason && data.reason.kind && data.reason.kind !== 'completed') { log('这一轮没正常结束：' + data.reason.kind); return }
      if (!text) return
      void deliver(session, text)
    }
  }

  try {
    if (typeof ctx.on !== 'function') throw new Error('ctx.on 不是函数')
    ctx.effect(() => ctx.on('session/event', onEvent), 'live2d-console: 会话事件')
    log('已订阅 session/event')
  } catch (error) { log('订阅会话事件失败：' + error.message) }

  // ---- 桌宠那边说太长时排队的台词：用模型压短，再写回 say.txt 让她说 ----
  // 桌宠的超限在 cache\say-queue.json 里，格式 { limit, text, at }。
  const sayQueue = join(root, 'cache', 'say-queue.json')
  let queueAt = 0
  let draining = false
  const drainQueue = async () => {
    if (draining) return
    draining = true
    try {
      if (!existsSync(sayQueue)) return
      let at = 0
      try { at = statSync(sayQueue).mtimeMs } catch { return }
      if (at === queueAt) return
      let job = null
      // 解析成功才记 mtime：桌宠还在写的时候读到半截，下一轮要能重来。
      try { job = JSON.parse(readFileSync(sayQueue, 'utf8')) } catch { return }
      queueAt = at
      const limit = Number(job && job.limit) > 0 ? Math.floor(Number(job.limit)) : 90
      let text = String((job && job.text) || '').trim()
      if (!text) return
      log('排队台词 ' + visibleCount(text) + ' 字，上限 ' + limit + '，交给模型压短')
      for (let round = 0; round < 3 && !fits(text, limit); round++) {
        const out = await summarize(null, text, limit)
        if (!out) break
        text = out
        log('第 ' + (round + 1) + ' 次压完：' + visibleCount(text) + ' 字')
      }
      if (!fits(text, limit)) {
        text = wholeSentences(text, limit)
        log('三轮压不进上限或还带省略号，退回整句截断（' + visibleCount(text) + ' 字）')
      }
      writeFileSync(sayFile, text.slice(0, 2000), 'utf8')
      log('写 say.txt（排队压短后，' + visibleCount(text) + ' 字）：' + text.slice(0, 40))
      // 处理完就撤掉队列，免得重启后又把同一句说一遍。
      try { unlinkSync(sayQueue); log('队列已撤掉') } catch (error) {
        try { writeFileSync(sayQueue, '', 'utf8') } catch {}
        log('撤队列失败，已清空避免重说：' + (error && error.message ? error.message : String(error)))
      }
    } catch (error) { log('处理排队台词失败：' + error.message) }
    finally { draining = false }
  }

  const tick = () => { void poll(); void drainQueue() }

  try {
    ctx.effect(() => {
      const timer = every(tick, 800)
      return () => stop(timer)
    }, 'live2d-console: 读 chat.log 与排队台词')
    log('已开始轮询 chat.log 与排队台词')
  } catch (error) {
    every(tick, 800)
    log('ctx.effect 不可用，退回裸定时器：' + error.message)
  }

  log('已挂上；目标会话：' + (givenTarget() ? ('指定 ' + givenTarget()) : '未指定，用最近活跃的会话'))
}
