# Codex Home Manager

![应用图标](Assets/codex-home-manager.png)

`Codex Home Manager` 是一个面向 Windows 的桌面工具，用来管理 Codex 的账号配置、共享会话仓、运行目录，以及 `Codex.exe` 的启动流程。

当前仓库只维护 Electron 版本客户端：

- 客户端技术栈：`Electron + Vue 3 + TypeScript + Element Plus`
- 旧的 C# / WinForms 客户端已经移除
- 当前可打包为 Windows 安装器 `exe`

这个工具主要解决几类实际问题：

- 同一台机器上需要维护多个 Codex 账号
- `auth.json` 和 `config.toml` 不想手工来回拷贝
- 想把不同来源的会话统一导入到一个共享仓
- 想把“账号库存”和“实际运行中的 CODEX_HOME”分开管理
- 想把账号内容集中存到 SQLite，只有使用时才落地成文件

## 主要功能

- 读取来源 `CODEX_HOME` 中的会话列表
- 将选中的会话导入共享仓
- 将共享仓同步到运行目录
- 将当前账号的 `auth.json` / `config.toml` 同步到运行目录
- 同步完成后直接启动 `Codex.exe`
- 使用 SQLite 管理账号内容，而不是长期依赖实体文件
- 支持新建空账号、导入账号目录、导出账号目录、重命名账号、删除账号
- 支持直接编辑当前账号的 `auth.json` 与 `config.toml`
- 支持为不同共享仓分别设置默认启动账号
- 持久化界面目录配置，重开软件后保留上次设置

## 界面预览

### 1. 目录设置

![目录设置截图](Assets/screenshots/readme-path-setup.png)

### 2. 常用流程与账号管理

![操作中心截图](Assets/screenshots/readme-workflow.png)

### 3. 会话区域

![会话区域截图](Assets/screenshots/readme-session-area.png)

## 适用场景

- 一台电脑上切换多个 Codex 账号
- 将历史会话导入到新的共享仓
- 维护统一的会话仓和独立的运行目录
- 把账号配置集中保存在数据库中，减少手动复制文件
- 为不同共享仓绑定不同默认账号

## 核心目录说明

界面里的几个主要目录含义如下：

| 项目 | 说明 |
| --- | --- |
| 会话来源目录 | 用来读取历史会话的源 `CODEX_HOME`。程序会扫描 `sessions/`、`session_index.jsonl`、`history.jsonl`、`state_5.sqlite` 等文件。 |
| 当前账号目录 | 当前选中账号临时落地后的目录。内容来自 SQLite，不建议手工长期维护。 |
| 账号库存目录 | 兼容旧目录式账号仓的入口。首次使用时可以从这里迁移历史账号。 |
| 共享仓目录 | 所有导入后会话集中存放的位置。可以理解为统一的会话仓。 |
| 运行目录 | 实际启动 Codex 时使用的 `CODEX_HOME`。共享仓内容和当前账号配置会同步到这里。 |
| Codex 程序 | `Codex.exe` 的路径。留空时程序会尝试自动查找。 |

## 数据存储设计

这个项目现在采用“数据库优先”的账号管理方式：

- `auth.json` 和 `config.toml` 的主存储位置是 SQLite
- 账号切换或导出时，才将数据库内容物化到目录
- 运行 Codex 时，再把当前账号内容写入运行目录
- 所以账号本身不依赖某个固定实体目录长期存在

### 默认本地数据位置

程序默认将管理数据保存在：

- `%LOCALAPPDATA%\CodexHomeManager\managed-accounts.db`
- `%LOCALAPPDATA%\CodexHomeManager\materialized-profiles\`
- `%LOCALAPPDATA%\CodexHomeManager\ui-settings.json`

其中：

- `managed-accounts.db`：账号、账号内容、共享仓默认账号映射等数据
- `materialized-profiles`：账号临时落地目录
- `ui-settings.json`：界面上的路径和选择项持久化配置

### 默认业务目录

首次启动时，界面会给出这些默认目录：

- 会话来源目录：`%USERPROFILE%\.codex`
- 账号库存目录：`%USERPROFILE%\.codex-profiles`
- 共享仓目录：`%USERPROFILE%\.codex-shared-store`
- 运行目录：`C:\codex-home-hybrid`

### SQLite 关键表

| 表名 | 用途 |
| --- | --- |
| `accounts` | 账号基础信息，如名称、模型提供方、启用状态 |
| `account_contents` | 真正保存 `auth.json` 与 `config.toml` 文本内容 |
| `shared_store_defaults` | 共享仓与默认启动账号的映射关系 |
| `materialized_targets` | 记录目录物化/同步状态 |
| `app_settings` | 应用级设置 |

## 推荐使用流程

### 场景一：第一次配置

1. 设置会话来源目录。
2. 设置共享仓目录。
3. 设置运行目录。
4. 选择或导入一个账号。
5. 点击“初始化共享仓”。
6. 点击“同步运行目录”。
7. 点击“同步并启动 Codex”。

### 场景二：导入历史会话

1. 选择要使用的账号。
2. 点击“读取来源会话”。
3. 在会话列表中选择一条会话。
4. 点击“导入选中会话”。
5. 全部导入完成后，点击“同步运行目录”。
6. 最后点击“同步并启动 Codex”。

### 场景三：管理账号

1. 在账号区选择一个账号。
2. 使用“保存当前账号”将当前目录中的配置写回数据库。
3. 使用“新建空账号”创建全新的账号记录。
4. 使用“编辑内容”直接修改 `auth.json` / `config.toml`。
5. 使用“导入账号目录”或“导出账号目录”做迁移。
6. 使用“重命名账号”或“删除账号”维护库存。
7. 使用“设为共享仓默认账号”给当前共享仓绑定默认账号。

## 会话导入与同步逻辑

程序在导入和同步时，主要会处理这些内容：

- `sessions/`
- `session_index.jsonl`
- `history.jsonl`
- `state_5.sqlite`
- `.codex-global-state.json`
- `auth.json`
- `config.toml`

### 导入会话时会做什么

- 从来源目录复制目标会话相关数据
- 更新共享仓中的 `session_index.jsonl`
- 更新共享仓中的 `history.jsonl`
- 更新共享仓中的 `state_5.sqlite`
- 按需刷新会话 `updated_at`
- 按需把工作区路径写入 `.codex-global-state.json`

### 同步运行目录时会做什么

- 将共享仓中的会话状态同步到运行目录
- 将当前账号的 `auth.json` / `config.toml` 写入运行目录
- 根据设置决定是否覆盖运行目录已有配置
- 确保最终启动的 Codex 使用统一、可控的 `CODEX_HOME`

## 项目结构

当前仓库的主要结构如下：

| 路径 | 说明 |
| --- | --- |
| `electron-app/` | 当前唯一维护的桌面客户端 |
| `electron-app/src/main/index.ts` | Electron 主进程入口 |
| `electron-app/src/main/ipc.ts` | IPC 注册与桌面能力桥接 |
| `electron-app/src/main/services/codex-manager.ts` | 会话读取、导入、同步、启动逻辑 |
| `electron-app/src/main/services/profile-store.ts` | SQLite 账号管理与账号物化逻辑 |
| `electron-app/src/main/services/settings-service.ts` | 界面设置持久化 |
| `electron-app/src/preload/index.ts` | 安全预加载桥接 |
| `electron-app/src/renderer/src/App.vue` | 主界面 |
| `electron-app/src/renderer/src/styles.css` | 界面样式 |
| `electron-app/scripts/bump-package-version.mjs` | 打包前自动递增补丁版本号 |
| `electron-app/scripts/run-with-modern-node.mjs` | 兼容较旧 Node 环境的运行包装脚本 |
| `Assets/` | 图标和 README 截图资源 |

## 开发与打包

### 环境要求

- Windows
- Node.js 20+
- npm

说明：

- 项目已经内置兼容脚本，直接使用 `package.json` 里的 npm 命令即可
- 即使本机 Node 版本偏旧，也不要手工改构建命令，优先使用现成脚本

### 安装依赖

```powershell
cd C:\codex\CodexHomeManager\electron-app
npm install
```

### 开发模式

```powershell
cd C:\codex\CodexHomeManager\electron-app
npm run dev
```

### 类型检查

```powershell
cd C:\codex\CodexHomeManager\electron-app
npm run typecheck
```

### 构建生产版本

```powershell
cd C:\codex\CodexHomeManager\electron-app
npm run build
```

### 打包 Windows 安装器

```powershell
cd C:\codex\CodexHomeManager\electron-app
npm run dist:win
```

### 打包输出位置

Windows 安装包会输出到：

```text
C:\codex\CodexHomeManager\electron-app\release\
```

例如：

```text
C:\codex\CodexHomeManager\electron-app\release\Codex Home Manager-0.2.4-setup.exe
```

### 版本号策略

每次执行下面这些打包命令时，都会自动递增补丁版本号：

- `npm run dist`
- `npm run dist:win`

例如：

- `0.2.4` 打包后会变成 `0.2.5`
- 不需要手工修改 `package.json` 版本号

## 使用注意事项

- 切换账号或同步运行目录前，尽量先关闭正在运行的 Codex
- `当前账号目录` 是临时物化目录，不建议把它当作唯一备份
- 批量导入多个会话后，建议再执行一次“同步运行目录”
- 如果 `Codex.exe` 没有自动识别出来，可以手工指定路径
- 安装包当前未做代码签名，在其他电脑上首次运行时，Windows 可能提示“未知发布者”

## 后续维护建议

如果你准备继续扩展这个项目，优先从这些文件开始看：

- `electron-app/src/renderer/src/App.vue`
- `electron-app/src/main/services/codex-manager.ts`
- `electron-app/src/main/services/profile-store.ts`
- `electron-app/src/main/ipc.ts`

这几个位置基本覆盖了界面、会话同步、账号存储和桌面能力桥接四条主线。
