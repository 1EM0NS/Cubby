# AGENTS.md

给 AI 协作者与新接手者的须知。**动手前先读这里。**

## 项目是什么

**Cubby** —— Windows 桌面整理助手。核心差异化：**只借用盒子区域内的点击，其余鼠标消息原封不动下发**，因此与 Wallpaper Engine 等动态壁纸零冲突（现有同类工具的通病就是抢输入，导致互动壁纸点不动）。

## 先读什么

1. `技术方案.md` —— 设计原则、桌面图层模型、Win32 API 清单、验收标准 A1–A8、里程碑门禁
2. `docs/工作日志.md` —— 已经做过什么、验证过什么、踩过哪些坑（最新在最上）
3. GitHub 上的 open issues 与里程碑 M0–M3

## 不可违反的四条（P1–P4）

| 编号 | 约束 | 理由 |
|---|---|---|
| P1 | **不注册全局鼠标钩子**；万不得已时必须无条件 `CallNextHookEx` 放行，禁止返回非零值 | 不破坏依赖钩子链的第三方软件 |
| P2 | 浮层只有盒子区域参与命中测试，其余区域必须点击穿透 | 这是本项目的立身之本 |
| P3 | **不 `SetParent` 到 WorkerW**，用独立顶层窗口 + 事件驱动置底 | `SetParent` 后鼠标事件常失效，且会与壁纸软件争 Z 序 |
| P4 | **文件实体永不移动**，只保存引用与展示位置 | 最坏只丢配置，不丢用户文件 |

违反会被 CI 的 `tools/scripts/guard.ps1` 拦下。确有必要时，在调用行**前两行内**加 `// guard-exempt: <原因>`，并且**必须先补一篇 ADR** 再动代码。

## 工作规矩

1. **不直推 `main`**，走短命分支 + PR。
   分支前缀：`spike/`（机制验证，允许失败）、`feat/`、`fix/`、`test/`、`ci/`、`docs/`、`chore/`
   ⚠️ **注意：这条规则靠自觉，没有技术强制。** 分支保护的 `enforce_admins = false`，
   仓库所有者（管理员）直推 `main` **不会被拒绝**。2026-09-19 已经因此发生过一次违规直推，
   记录见 `docs/工作日志.md` 会话 5。违规后必须在工作日志里留痕。
2. 提交信息用 Conventional Commits：`type(scope): 简述`，type 与 scope 用英文，正文可用中文
3. 每个功能一个闭环：Issue → 分支 → 实现 → 本地跑回归清单 → PR（写清"怎么验证的 + 实际结果"）→ CI 绿 → 合并关 Issue
4. **每次会话结束必须往 `docs/工作日志.md` 追加一条记录**（最新在最上），包含：目标、做了什么、验证与证据、遗留与下一步。**禁止修改历史条目。**
   日志补记可直接提 PR，不必单独开 Issue；只有功能与修复类改动才必须挂 Issue。
5. 跨过里程碑门禁才打 tag：M0 → `0.1.0`，M1 → `0.2.0`，M2 → `0.3.0`，M3 → `1.0.0`

## 环境注意事项（都是踩过的坑）

- **代理**：本仓库 `.git/config` 已把 `http.proxy` / `https.proxy` 置空，用于覆盖全局那个失效的 `127.0.0.1:7890`。若某天需要走代理，用 `git -c http.proxy=127.0.0.1:端口 push` 临时指定，**不要去改全局配置**。
- **推送用 `gh`**：已登录账号 `1EM0NS`，token 含 `repo` 与 `workflow` 权限。
- **PowerShell 5.1 三个坑**：
  1. 空字符串参数会被丢弃（`git config --local http.proxy ""` 实际不写入，且 `--get` 返回空会造成"已生效"的假象）
  2. 参数模式下 `-f title=$m.Title` 不做成员访问，会提交字面量 `System.Collections.Hashtable.Title`；描述里的 ASCII 双引号会破坏参数解析
  3. 读无 BOM 的 UTF-8 脚本会按 ANSI 解析，中文直接报语法错误
  → **结论：给 `gh` 传中文内容一律用 `--body-file` 传文件；`.ps1` 存盘时带 BOM。**
- 本机没装 `pwsh`（PowerShell 7），CI 的 `windows-latest` 上有。
- **`gh pr merge --squash --delete-branch` 不会自动切回本地 `main`**：合并后本地仍停在已被删除的分支上，紧接着 `git pull` 会报 `no such ref was fetched`，很容易误判为"合并失败"。
  → 判断合并结果一律以 `gh pr view <编号> --json state,mergedAt` 为准，**不要看 git 本地状态**。收尾动作：`git switch main && git pull && git branch -D <分支>`。

## 常用命令

```powershell
# 本地跑设计原则守卫（本机用 powershell 5.1）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\scripts\guard.ps1

# 开分支、提交、推
git switch -c feat/xxx
git add -A; git commit -m "feat(scope): ..."; git push -u origin HEAD

# 提 PR、看 CI、合并
gh pr create --body-file <文件>      # 中文正文一律用文件，见上文编码坑
gh run list --repo 1EM0NS/Cubby --limit 5
gh pr merge --squash --delete-branch

# 查看待办
gh issue list --repo 1EM0NS/Cubby --limit 30
```

### Cubby 自带的自动化验收（报告都写到 `artifacts/`）

```powershell
$app = ".\src\Cubby.App\bin\Release\net8.0-windows\Cubby.App.exe"

& $app --selftest              # 命中测试：盒子内拦截、盒子外穿透（写 hittest-summary.md）
& $app --selftest-interact     # 盒子交互：拖动 / 缩放 / 折叠 / 锁定（写 interaction-report.md）
& $app --selftest-drop         # 拖入：生成引用 + 原文件指纹不变（写 drop-report.md）
& $app --selftest-menu         # 条目菜单 / 拖出：命令效果 + 原文件指纹不变（写 menu-report.md）
& $app --selftest-shell        # 托盘 / 自启 / 样式 / 显隐 / 资源基线（写 shell-report.md）
& $app --selftest-adopt        # 桌面图标吸附：读取 + 吸附前后位置一字不差（写 adopt-report.md）
& $app --selftest-snapshot     # 布局快照：新建 / 列出 / 还原 + 还原前后指纹一致（写 snapshot-report.md）
& $app --selftest-map          # 文件夹映射：实时增删 + 原目录指纹不变（写 map-report.md）
& $app --selftest-search       # 盒子内搜索：500 条规模查询耗时 + 溢出后重建（写 search-report.md）
& $app --selftest-rules        # 归类规则：五类条件命中 + 预览/应用/撤销 + 文件不动（写 rules-report.md）
& $app --selftest-desktop-icons # 桌面图标显隐：三个入口 + 崩溃兜底 + 系统矩阵（写 desktop-icons-report.md）
& $app --dump-monitors         # 显示器枚举与分配计划（写 monitors.txt）
& $app --dump-desktop-icons    # 桌面图标读取明细：名称 / 坐标 / 匹配到的文件（写 desktop-icons.txt）
& $app --dump-state            # 运行状态：盒子渲染、命中区域、鼠标计数、常驻资源（写 state.txt）
& $app --diagnostics           # 常驻托盘 + 打开诊断面板
& $app                         # 无参 = 常驻形态（只有托盘图标，不占任务栏）

# 窗口链置底断言（A4）
.\tools\ZOrderProbe\bin\Release\net8.0-windows\ZOrderProbe.exe --assert-behind --exe Cubby.App --title "Cubby 浮层"

# 鼠标钩子链基线对比（A1/A3）：先在 Cubby 未运行时采集基线
.\tools\HookProbe\bin\Release\net8.0-windows\HookProbe.exe --out artifacts\hookprobe-baseline.json
```