window.__ModuleLoader__.load({ id: 'dsh-live2d-console', factory: require => {
  const React = require('react')
  const h = React.createElement
  const NS = 'live2d-console'
  const inject = ['slots', 'locale', 'configForms']
  const dictionaries = {
    zh: {
      title: 'dshlive2d控制台',
      summary: 'Live2D 桌宠跟随 DSH 启停，以及桌面会话桥。',
      noForm: '这个宿主没有提供配置表单服务，开关不可用。',
      follow: '跟随 DSH 启停',
      followHint: '开：DSH 启动时她出现，DSH 关闭时她一并收起。关：DSH 启动后不拉她，退出时也不动她。',
      session: '桌面输入框里说的话，投给设置里指定的会话；没指定就投给最近活跃的会话。桥不会新建会话。',
      save: '保存', saving: '保存中…', saved: '已保存，立即生效。',
      failed: '没能保存，请检查宿主是否允许改这项配置。',
      readOnly: '当前设置为只读。', loading: '正在读取设置…',
    },
    en: {
      title: 'dshlive2d console',
      summary: 'Whether the Live2D pet follows DSH start/stop, plus the desktop session bridge.',
      noForm: 'This host exposes no configuration form service; the switch is unavailable.',
      follow: 'Follow DSH start/stop',
      followHint: 'On: she appears when DSH starts and closes when DSH closes. Off: DSH starts without her, and DSH exit leaves her alone.',
      session: 'What you type in the pet\'s input box goes to the session named in settings, or the most recently active one. The bridge never creates a session.',
      save: 'Save', saving: 'Saving…', saved: 'Saved, effective immediately.',
      failed: 'Could not save. Check whether the host allows editing this setting.',
      readOnly: 'Settings are read-only.', loading: 'Loading settings…',
    },
  }

  function Card ({ view, form, t }) {
    if (!form) return h('p', null, t('noForm'))
    const subscribe = React.useCallback(listener => form.subscribe(listener), [form])
    const read = React.useCallback(() => form.getSnapshot(), [form])
    const state = React.useSyncExternalStore(subscribe, read, read)
    const [follow, setFollow] = React.useState(true)
    const [saving, setSaving] = React.useState(false)
    const [message, setMessage] = React.useState('')
    const [failed, setFailed] = React.useState(false)
    const draftRevision = React.useRef()
    const dirty = React.useRef(false)
    React.useEffect(() => {
      if (!state.value || dirty.current) return
      setFollow(state.value.followDsh !== false)
      draftRevision.current = state.revision
    }, [state.value, state.revision])
    if (view === 'summary') return t('summary')
    if (state.status !== 'ready') return h('p', null, t('loading'))
    const disabled = !state.writable || saving
    async function save (event) {
      event.preventDefault()
      setSaving(true); setMessage(''); setFailed(false)
      try {
        const ok = await form.mutate([{ op: 'set', path: ['followDsh'], value: follow }], draftRevision.current)
        if (!ok) throw new Error('rejected')
        dirty.current = false
        const latest = form.getSnapshot()
        draftRevision.current = latest.revision
        setFollow(latest.value?.followDsh !== false)
        setMessage(t('saved'))
      } catch {
        draftRevision.current = form.getSnapshot().revision
        setFailed(true); setMessage(t('failed'))
      } finally { setSaving(false) }
    }
    const box = {
      border: '1px solid var(--dsw-alias-border-l1, #ffffff14)',
      borderRadius: '10px',
      padding: '14px 16px',
      background: 'var(--dsw-alias-bg-layer-1, #ffffff06)',
      display: 'grid',
      gap: '10px',
    }
    return h('div', { style: { display: 'grid', gap: '14px', maxWidth: '640px', fontSize: '13px' } },
      h('div', { style: box },
        h('label', { style: { display: 'flex', alignItems: 'center', gap: '10px', fontWeight: 600 } },
          h('input', {
            type: 'checkbox',
            checked: follow,
            disabled,
            onChange: event => { dirty.current = true; setMessage(''); setFollow(event.target.checked) },
          }),
          t('follow')),
        h('p', { style: { margin: 0, opacity: 0.8, lineHeight: 1.6 } }, t('followHint')),
        !state.writable ? h('p', { role: 'status', style: { margin: 0 } }, t('readOnly')) : null,
        message ? h('p', { role: failed ? 'alert' : 'status', style: { margin: 0 } }, message) : null,
        h('button', {
          type: 'button',
          disabled,
          onClick: save,
          style: {
            justifySelf: 'start', padding: '6px 16px', borderRadius: '8px', cursor: disabled ? 'not-allowed' : 'pointer',
            border: '1px solid var(--dsw-alias-border-l2, #ffffff22)',
            background: 'var(--dsw-alias-bg-layer-2, #ffffff0d)',
            color: 'var(--dsw-alias-label-primary, inherit)', fontFamily: 'inherit', fontSize: '13px',
          },
        }, t(saving ? 'saving' : 'save'))),
      h('div', { style: box },
        h('p', { style: { margin: 0, opacity: 0.8, lineHeight: 1.7 } }, t('session'))))
  }

  function apply (ctx) {
    const t = ctx.locale.bind('live2d')
    ctx.effect(() => ctx.locale.register('live2d', dictionaries))
    const form = ctx.configForms.get(NS)
    ctx.effect(() => ctx.slots.inject('settings.section', () => ctx.slots.register({
      name: 'settings.section', id: 'live2d-console', order: 46,
      label: () => t('title'), locale: 'live2d',
    }, props => h(Card, { ...props, form, t }))))
  }

  return { apply, inject }
} })
