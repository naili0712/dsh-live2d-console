# dsh-live2d-console（dshlive2d控制台）

把一只 Live2D 桌宠接进 DeepSeek Harness：桌宠头顶输入框里说的话会投给 DSH 会话，会话一轮结束再把回复压成一句气泡台词说回桌面；DSH 设置页里还有一个开关，决定她跟不跟 DSH 一起起落。

桌宠本体（`pet\`）一并放在组件里，不再散落在用户目录。

## 能做什么

- **会话桥**：桌面输入 → DSH 会话；DSH 回复 → 桌面气泡。桥不新建会话，也不新建工作区。
- **跟随启停**：DSH 设置 → 左侧「dshlive2d控制台」里一个开关；关掉后 DSH 启动不拉她，退出也不动她。
- **说话长度**：一次说太多会被退回给模型压短再说，压出来的必须是完整句子，不会截半句、不会挂省略号。
- **台词历史**：她说过的每句话都记在本地，设置面板里能翻最近两百条。

## 怎么装

1. 把整个组件目录放进 DSH 的 `mods\` 下，在 DSH 的 profile 里加一条 `link:` 依赖。
2. 桌宠本体还需要两样东西，本仓库**不包含**，要自己按各自许可获取：
   - Live2D Cubism SDK for Native → 放进 `pet\sdk\`；
   - Live2D 模型 → 放进 `pet\model\`（见 `pet\model\获取模型.md`）。
3. 编译桌宠：`pet\setup.ps1` 会检查缺什么、编译并自检。
4. 重启 DSH。设置页出现「dshlive2d控制台」就接上了。

更细的安装与接入步骤（包括交给 AI 自动装）见 [pet/AGENTS.md](pet/AGENTS.md)。

## 目标会话怎么选

只有两个来源，**桥不会新建会话，也不会新建工作区**：

1. `pet\settings.json` 里的 `SessionTarget`（指定会话）；
2. 15 分钟内最近活跃的会话（排除 `aurabot` / `qq-chats` 分组）。

两边都没有就等着，日志里节流记一句「没有可投的会话，这条先留着」，等对话活跃起来再投。

## 设置页

位置：**DSH 设置 → 左侧导航「dshlive2d控制台」**（slot `settings.section`，id `live2d-console`）。

| 开关 | 含义 |
| --- | --- |
| 跟随 DSH 启停 | 开（默认）：DSH 启动时她在，关闭时她一并收起。关：DSH 启动后不拉她，退出也不动她 |

关闭走她自己的本地接口：`POST <pet\api.json 的 url>/call/quit`，带 `Authorization: Bearer <token>`，只连 127.0.0.1。DSH 退出时若异步来不及，`process.on('exit')` 会同步 `taskkill FishPet.exe` 兜底。

## 桌宠设置面板 → 说话与交流

右键托盘 →「设置…」→ 第四个页签「说话与交流」：

| 项 | 说明 |
| --- | --- |
| 字数上限 | 一次 `say` 调用可显示的可见字数上限（`〔〕` 内的神情动作不计入，多行合并计算）。默认 90，0 表示不限，上限 2000。**超出时本次调用直接失败**并返回字数提示，不会自动压缩或截断；调用方应拆分为多次调用、每次一句 |
| 说过的 | 她每次在桌面上说的话都记在 `pet\cache\say-history.jsonl`，一行一条：时间 / 正文 / 气泡数。面板显示最近 80 条，文件只留最近 2000 行 |

## 配置

`cordis.patch.yml` 里给初值，设置页改的那一项会写回 DSH 的配置层：

| 键 | 含义 | 默认 |
| --- | --- | --- |
| `petRoot` | 桌宠根目录 | 组件目录旁的 `pet\` |
| `dataDir` | 桥生成物目录（`bridge.log`） | `mod-loads-12\` |
| `followDsh` | 跟随 DSH 启停 | `true` |

## 路径约定

- 桥生成物 `bridge.log` 写 `dataDir`。
- `pet\cache` 常做目录联接指向 `..\mod-loads-12`，桌宠照常读写 `cache\`。
- 桌宠用 `AppContext.BaseDirectory` 向上找同时含 `renderer` 与 `model` 的目录当根，`pet\` 内结构不能拆散。
- 命名管道名由 `用户名 | bin 目录大写` 的 sha256 前 20 位生成，两端都按 `BaseDirectory` 算。

## 目录

```
dsh-live2d-console/
  package.json              插件声明（dsh.bundle + dsh.client）
  cordis.patch.yml          补丁层，配置都在这里
  index.js                  host：会话桥 + 跟随启停
  client.js                 设置页分区卡片（web client）
  pet/                      桌宠本体
    README.md               桌宠自己的说明
    AGENTS.md               给 Agent 的安装与接入指引
    setup.ps1               检查、编译、自检
    src/                    桌宠程序（C#）
    renderer/               渲染器源码（C++，需自备 Cubism SDK）
    prompt/                 身体说明
    sdk/  model/            自备：SDK 与模型，不随仓库分发
```

## 许可与来源

**本仓库是 [Ssssshaiiko/WHALEL2D](https://github.com/Ssssshaiiko/WHALEL2D) 的二创分支。**

- 桌宠本体（`pet\` 及其修改）来自 WHALEL2D，按其许可使用与分发；
- DSH 控制台部分（`index.js` / `client.js` / `cordis.patch.yml` 与设置页）由 [naili0712](https://github.com/naili0712) 编写；
- 模型与 Live2D SDK 不包含在本仓库里，各自遵循原作者与 Live2D 的许可；
- 整体遵循 [PolyForm Noncommercial License 1.0.0](LICENSE)，**仅限非商业用途**。上游作者另外允许个人用于直播、录播、视频等自己的内容创作（含平台收益），但不包括出售插件或改版、付费安装定制、企业宣传等。

`Required Notice: Copyright (c) 2026 Ssssshaiiko (https://github.com/Ssssshaiiko/WHALEL2D)`

## 回滚

1. 从 profile 的 `package.json` 删掉 `dsh-live2d-console` 这条 dependency 与 `bundles` 里的包名。
2. 删掉 `node_modules\dsh-live2d-console` 链接。
3. 组件目录整体移走即可；本体在组件内 `pet\`，不需要另外恢复。

改完配置要重启 DSH 才生效。
