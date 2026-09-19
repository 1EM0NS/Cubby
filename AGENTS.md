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
- **⚠️ `git pull` / `git checkout` 会丢文件，务必用免沙箱的方式跑，跑完立刻数文件数。**
  2026-09-20 连撞两次：`git pull` 之后 `git status` 把 `src/**` 下 97 个文件报成 ` D`，
  而 `HEAD` 与索引都是完整的——**这是真的丢了工作区文件**，不是索引脏。
  沙箱里的命令要写很多文件时会静默失败一部分。
  收尾动作固定成这三步（最后一步是硬要求，不是可选）：
  ```bash
  git switch main && git pull
  git status --short                 # 不该出现成片的 " D "
  ls src/Cubby.App | wc -l           # 应该 50 上下；只剩个位数就是丢了
  ```
  真丢了就 `git restore --worktree .`（免沙箱）——索引 == HEAD，内容无损。
  别用 `git checkout -- .` 之外的破坏性命令，也别 `git add` 之后才发现在删文件。
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

### 跑验收前先确认桌面没被遮挡（`--selftest-desktop-icons` 会因此假红）

`--selftest-desktop-icons` 会**拿真实桌面**做命中断言：在盒子标题栏上取一点，
期望 `WindowFromPoint` 命中我们的浮层。而浮层按设计（A4/P3）**永远在应用窗口之下**，
所以只要有任何应用窗口盖住那个点，点击就"正确地"属于那个窗口，断言必然失败——

```
隐藏状态下：盒子内仍归浮层、盒子外仍穿透 | **FAIL** | (2123,122) Chrome_RenderWidgetHostHWND 期望 浮层
```

**这是环境问题，不是回归。** 2026-09-20 实测过一次：把没改动的 `main`（71f3f46）拿去跑，
失败点与探测结果一字不差；把盒子临时挪到未被遮挡的区域（或关掉/最小化遮挡窗口）后立刻 PASS。
排查手法：`tools/ZOrderProbe/bin/Release/net8.0-windows/ZOrderProbe.exe --json`，
看落点的窗口矩形归属（注意它输出的是 **DPI 虚拟化后的逻辑坐标**，物理坐标要乘 `DpiScale`）。

`--selftest` 不受影响：它自造一个受控背景层并置顶，刻意绕开用户桌面状态。

### 出问题了先看日志

- 崩溃与未处理异常写在 `%AppData%\Cubby\logs\cubby-yyyyMMdd.log`：按天分文件，
  保留最近 **14 天**、总量上限 **8MB**，超额从最老的开始删。文件头记录**版本 / OS / 运行时**三要素。
- 三处入口的处理策略**刻意不同**，别当成不一致去"统一"：
  `DispatcherUnhandledException` 落盘 + 非模态提示后 `Handled=true`（常驻程序不该因为一次 UI 异常整体退出）；
  `AppDomain.UnhandledException` 落盘 + **模态**提示（进程随后就没了，值得拦住）；
  `TaskScheduler.UnobservedTaskException` **只落盘不弹窗**（它不是一个崩溃，每次弹窗就是骚扰）。
- **顺序是需求的一部分**：先还原桌面图标标记 → 再写日志 → 最后提示用户。
  图标是我们留在用户机器上的副作用，优先级高于"留证据"；改 `CrashReporter.Handle` 时不要调换。
- `CrashLog` 的所有公开方法**都不抛异常**，写不进去就返回 `null`——日志问题绝不升级成崩溃问题。

### 挂机采样（A7）判据与「工作集」那个口径坑

- `--soak <分钟>` 跑真挂机，`--selftest-soak` 是 24 秒的短程自检；**两者是同一条代码路径**，只差时长与间隔
  （`--soak-interval <秒>` 可改间隔，默认 30 秒）。判定逻辑在 `Cubby.Core/Diagnostics/SoakAnalysis.cs`（纯函数、有单测）。
- 判据：句柄 / GDI / USER / 线程数**既不单调增长、首末差也在噪声阈值内**（句柄 ±8，其余 ±4），
  私有字节**末值 ≤ 120MB 且斜率 ≤ 1MB/分**（或首末差 ≤ 8MB，容忍 GC 抖动）。跳过前 2 次预热。
- **采样期间会真的改布局**（折叠 / 展开 + 挪位置，走 `OnBoxChanged` + `ApplyBoxUpdate`），
  否则「不增长」说明不了任何事。跑完会 `layout.Apply(原文档)` 还原，不会把你的摆放留在那儿。
- **⚠️ 工作集那一项是「只报不判」，别以为它是绿的**：本机稳态工作集 **139~150MB**，
  超过技术方案第 8 章的 120MB；但同一时刻**私有字节只有 96~106MB**（在预算内）。
  两个口径差约 40MB，第 8 章原文写的是「常驻内存」而 #41 写的是「工作集」——**口径歧义见 issue #46**。
  报告里两个数都会列出、工作集单独标 ⚠。若判定要改成按工作集口径，把
  `SoakAnalysis` 里那一项的 `Gating` 改成 `true`，`--selftest-soak` 会立刻变 FAIL。

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
& $app --selftest-appearance   # 视觉打磨：像素级 P2 断言（盒子外 alpha=0）+ 预览图（写 appearance-report.md / appearance-preview.png）
& $app --selftest-coexist      # 同类软件共存（A8）：造真实同名进程，证明只提示不抢占（写 coexist-report.md）
& $app --selftest-onboard      # 首次运行引导：出现 / 跳过 / 重置三条路径 + 托盘「使用指引」（写 onboard-report.md）
& $app --selftest-crashlog     # 崩溃日志：注入假异常落盘 + 文件头 + 轮转/保留 + 提示窗可操作（写 crashlog-report.md）
& $app --selftest-soak         # 稳定性采样门禁（A7）短程自检：判定器 + 采样链路（写 soak-report.md）
& $app --soak 480              # 真挂机 480 分钟（A7 的完整结论只能由它给出）；--soak-interval <秒> 改采样间隔
& $app --reset-onboarding      # 重置引导标记，下次启动重新出现欢迎窗（换机、演示前重放用）
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