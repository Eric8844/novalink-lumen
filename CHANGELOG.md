# Changelog

## v1.5.1（2026-11-07，已发布）

> v1.5.0 发布之后进入源码仓的改动，统一归入 1.5.1。
>
> 安装包 `NovaLinkLumen-WinUI-Setup-1.5.1.exe`（69,398,948 字节，sha256 `F909B785741483C44AE61B4BF035B94BE061498477B833CCD74ECE07484F6F2A`）；Release：[正式版仓](https://github.com/Eric8844/novalink-lumen-releases/releases/tag/v1.5.1) · [源码仓](https://github.com/Eric8844/novalink-lumen/releases/tag/v1.5.1)。

### 新增

- 主页账号卡新增 3D 人物：右上角显示一个会缓慢转身、轻微起伏的方块人（Minecraft 比例：头 8×8×8、身体 8×12×4、手脚 4×12×4），皮肤就是当前账号自己的皮肤 —— 微软 / NovaLink 账号走在线皮肤（下载后缓存到 `LauncherData\skins\<账号Id>.png`，12 小时后台刷新一次），离线账号优先用本地上传的皮肤文件，都没有时用内置的史蒂夫默认皮肤（64×64，含脸、头发、衣服）；支持 64×64 与 64×32 老皮肤，Slim / 经典手臂按账号皮肤模型自动切换
- 人物绘制细节：逐面 UV 贴图 + 按法线的柔和明暗、脚下一圈投影、身后一团薄荷微光，卡通像素用最近邻采样保持硬边；只有加载完成后才切图，加载失败自动回落默认皮肤，不会出现空白块
- 3D 人物可以拖动旋转：按住人物左右拖转、上下拖俯仰（±34°），松手 2.5 秒后缓慢恢复自动摇头；指针变成四向拖动样式
- 默认史蒂夫改用官方 64×64 皮肤贴图（随程序放在 `Assets\steve.png`），不再用程序化画的近似色块；贴图缺失或加载失败时才回落内置像素图
- 背景音乐记住上次的位置：退出时记下第几首、听到第几秒、当时是播放还是暂停；下次打开启动器回到同一首同一位置，上次是暂停的就停在原位不自动响。播放/暂停、上一首/下一首、拖动进度、每 10 秒都会顺手存一次，异常退出也最多只丢十来秒
- 设置 →「外观与性能」的音乐卡片新增两个开关：「记住上次听到哪里」与「启动时自动播放」（关掉自动播放后，即使上次是在播，这次也会先停在原位等用户自己点播放）
- 液态玻璃全覆盖：发现页与库页原先漏掉的卡片补上了磨砂层（透镜收集从「最多 7 张、跳过占视口 30% 以上的大容器」改成「最多 28 张、0.55 视口阈值、按视口相交裁剪、允许行卡里嵌卡」）；库页 100 多行模组现在**每一行**都是一条玻璃带，长列表滚动时行卡与背景透镜保持同步
- 玻璃边缘折射：背景层沿每张卡的圆角内缘画 1.5px 渐变折射环、外侧再压 0.5px 暗边，卡片边界在深色星空背景上会像一块厚玻璃那样把边缘光折出来
- 鼠标跟随柔光：指针在卡片、按钮、弹窗边缘移动时，背景层会在指针附近提亮一圈 0.16 的柔光并沿所覆盖控件的内缘画一道折射光，停住 0.6 秒后光带淡出；整屏 16ms 节流、静止即停表，画质挡位越低光越弱
- 画质挡位 + 自动检测动画：设置 →「外观与性能」→「玻璃材质」提供 自动 / 质量 / 平衡 / 性能 / 关闭 五枚胶囊；选「自动」或点【重新检测】会先跑 1.4 秒的分步扫描动画（读取 CPU 核心数、物理内存、显卡与分辨率），然后把推荐挡位与检测结果摆出来。判定：≥8 核且 ≥16GB 走「质量」，≤3 核或 <8GB 走「性能」，其余「平衡」；日志记一行 `自动画质：<挡位>（评分 … · 内存 … · CPU … · GPU … · 分辨率 …）`

### 变更

- 液态玻璃改为 Lens 材质（参考 `ruiqichenbiec.github.io/design-systems`）：不再给每张卡生成 SDF 位移贴图 + 三通道色散（原来每帧每卡三次位移采样，4K 下 GPU 吃满，边缘还会被拉出一圈假亮边），改成整屏半分辨率模糊一次、所有卡片共享同一张 frost 纹理，卡片只叠「冷白薄雾 + 1px 内侧顶部高光 + 菲涅尔扫光」。代价是去掉厚折射与色散，观感更接近参考站的轻薄玻璃
- 玻璃令牌重做：深色主题是薄烟灰玻璃（白雾透明度约 0.10 量级，不再把模糊层整个盖住），浅色主题是高调奶白（透明度 0.52~0.65）；描边顶部提到 0.45 白。设置页「液态玻璃引擎」改名「玻璃材质」，删掉已失效的「折射强度」「色散」两个滑杆（保留画质自适应 / 边缘高光 / 镜面反射）

### 修复

- 卡片顶部那条 1.5px 高光横线全部去掉（首页、版本、库、发现、论坛）：用户反馈「卡片右上角有一条横线」。卡片本身保留玻璃描边与底色，只是不再单独压一条线
- 弹窗顶部高光从 1.5px 硬线改成 60px 高的柔光渐变（`BrushGlassTopBand`），浅色下不再像被裁了一刀
- 点了「暂停」但重启后又自己响起来：`MediaPlayer` 的 `PlaybackState` 要过几十毫秒才变，点完立刻存状态会读到旧的「播放中」。现在点播放/暂停时用「意图」覆盖一次再落盘
- 卡片按压时的"分层"重影（第二次修，这次是根治）：卡片悬停/按压不再改 `Scale`，只做合成层 `Translation` 位移 —— 缩放会让卡片自己的描边内缩几个像素，而背景玻璃透镜永远慢半拍，按下时就会同时看到原位置的旧轮廓和缩小后的新描边；位移不改变元素的采样矩形，所以无论透镜有没有跟上都不可能多出一圈边（发现页、版本、库、首页、论坛的卡片与按钮一起生效）
- 发现页进入时的加载动画原本贴在页面顶部、压住标题，改为在内容区居中显示
- 发现页筛选栏下面还会再冒出一层加载动画（骨架卡 + 居中提示同时在屏）：现在只保留一条居中加载提示，结果区的骨架卡整套去掉，加载完成直接上内容
- 背景玻璃透镜只跟随真正会缩放的卡片做姿态补偿，跳过被别的玻璃卡包住的嵌套卡（之前内外两层各按自己的中心缩，按压时边对不齐）
- 分段切换（发现页「内容 / 启动器插件」与类型栏、论坛「话题 / 动态」、库页「模组 / 世界 / 资源包 / 光影」）改成**选中项自己带一枚胶囊底色**：不再插一条独立的滑动药丸，因此不会出现药丸停错位置、切换后留半块色斑。前一代「独立药丸 + 精确几何定位」在发现页（分段栏在横向 ScrollViewer 里）会抛 `COMException (0x800F1000)` 直接让启动器退出——这次换写法同时根治了错位与崩溃
- 分段切换动画减负：切换只给新选中项一个 180ms 的轻微压下-回弹，不再整条药丸滑动；旧版残留的指示器元素会在页面加载时自动清掉

### 修复（第二轮：分段栏胶囊 / 库页叠层 / 假高光）

- 库页标签行的「穿过去」：上一版在每次点页签时给按钮写 `CornerRadius = 999`，而 WinUI 不会把圆角 clamp 到按钮高度的一半——999 会让圆角路径溢出按钮边界、退化成一个压到相邻页签上的大椭圆，加上「当前实例」外框里还套着一个自带圆角的 ComboBox，两层边框同时可见就像一层穿过了另一层。现在标签行拆成两条独立的圆角轨道（`BrushFillSubtle` 底 + `BrushBorderSubtle` 描边 + 圆角 22，一高一矮各自留 4~6px 内边距），ComboBox 的背景/边框/各指针状态资源全部透明，页签按钮圆角在 `SizeChanged` 里取 `max(8, 高度 / 2)`，永远只有一层圆角
- 分段切换的胶囊点一下就消失：WinUI `Button` 的 PointerOver/Pressed 视觉状态会用主题资源覆盖 `Background`（在 `button.Resources` 里覆写 `ButtonBackground*` 会抛 `0x800F0902`，改不动）。现在给分段按钮套一层自定义 `ControlTemplate`——胶囊底/边/圆角全部走 `TemplateBinding`，再叠一层只做悬停增亮的 `HoverVeil`——`Apply()` 只写 Background / BorderBrush / Foreground，胶囊在任何指针状态下都保持不透明选中色
- 设置页滚动时液态玻璃消失：删掉 `SettingsScroll_ViewChanging` 把背景切成「无模糊兜底图」的逻辑与 140ms 后复位的定时器，滚动过程中玻璃保持模糊与高光
- 卡片悬停的「假高光」：删掉整卡铺满的线性渐变高光层与沿整卡描边的菲涅尔环，换成一段沿卡片边缘、方向始终朝向鼠标的镜面光弧（宽柔光 + 1.9px 亮弧），鼠标移开即熄灭，不再有整片发白的假光（该光弧在「修复（第三轮）」里又重做为整卡受光）

### 修复（第三轮：卡片受光）

- 卡片高光从「边缘描线」改为**整卡受光**：光标成为唯一光源，光按「光标到卡片矩形最近点的距离」衰减（`reach = max(w,h)*0.42 + 120`，smoothstep 后再平方，超出光程直接不画），再沿「卡片中心→光标」方向铺满卡面、在光标落点叠一团柔光、迎光边缘收一圈高光，三层共用同一 amount 且全部用 `CanvasBlend.Add` 叠上去；屏幕另一头的卡片不再发亮，「边缘一圈亮线、卡内一片黑」的割裂感一并消失

### 修复（第四轮：幽灵玻璃卡）

- 切栏后残留「幽灵玻璃卡」：发现页从「内容」切到「启动器插件」再进论坛，页面上会多出几块没有内容的空玻璃卡，还和真卡片叠在一起。原因是玻璃透镜只检查卡片自己的 `Visibility`，而 XAML 子元素并不继承父级的 `Collapsed` —— 内容面板折叠后，里面的 12 张模组卡与商店骨架卡依旧是 `Visible`、`IsLoaded`，最后一帧的宽高与坐标也都还在，背景层于是每帧照旧在原位画透镜。现在收集透镜前会沿视觉树向上检查到窗口根（`GlassPanes.cs:288` 的 `IsEffectivelyVisible`），任何一层 `Visibility != Visible` 或 `Opacity <= 0.01` 都算不可见：扫描时先过滤，每帧出图前再过滤一次，切栏当帧空卡就消失，不用等 800ms 重扫。修复后插件页只剩自己页面的卡片，内容页 13 张（hero + 12 张模组卡）与论坛、首页、库页、设置页的透镜数量全部正常

### 修复（第五轮：库页上滑鬼影 / 行卡上屏延迟）

- 库页列表上滑后，标题栏与「模组 / 全选 / 批量启用」区域会浮出一块行卡形状的淡玻璃带与描边（鬼影）：透镜收集只判断卡片是否在**页面**视口里，行卡滚出列表自己的 `ScrollViewer` 之后仍然在页面内，于是被当作可见卡继续出 pane，那块毛玻璃被画到标题栏位置，透过半透明的行卡看起来就是「上滑后的残影」。现在 `GlassPanes` 会沿视觉树找到行卡所属的 `ScrollViewer`，在扫描时按滚动视口过滤、出图时把 pane 矩形裁到滚动视口内（`ClipToScrollViewport`），滚出视口的行卡不再占透镜名额，滚动过程与停下后标题区都保持干净
- 新行卡上屏时「先透明底、几百毫秒后才补上玻璃」：`AuroraBackdrop.RefreshPanes()` 让透镜可以立刻重建，库页每次渲染完（`UpdateSelection()` 之后）调用 `GlassPanes.Rescan(this)` 立即重扫；定时重扫阈值 800ms → 300ms，滚动中新进入视口的行卡最多晚 0.3 秒补玻璃

### 修复（第六轮：库页统计口径）

- 库页那行统计（原「12 个实例 · 545 模组 · 40 世界 · 51 资源 · 占用 14.0 GB」）在切换版本时纹丝不动：它统计的是**全部实例的合计**（`LibraryService.Overview` 对传入的所有实例求并集），而且 `LibraryPage_Loaded` 只算一次，切换实例、点刷新、外部增删文件都只重画列表、不碰这行。现在改成**只统计当前实例**（模组 / 世界 / 资源 / 占用都按当前实例算，末尾保留「N 个实例」），并且跟着列表一起重算：切换实例、点刷新、`FileSystemWatcher` 触发的自动刷新、安装/删除内容后数字立刻同步。数据直接取本次扫描的 `snapshot`，不额外扫盘

### 变更（第二轮）

- 玻璃折射从「整卡放大 1.006~1.012」加强为连续放大 `1.022 + 0.012 × 强度`，取样中心再叠一个跟着鼠标走的 1.2% 漂移：背景在玻璃后面随光标轻轻滑动。试过「只有边缘一圈做折射」的方案，因为会出现一圈明显的阶梯分界线已撤回

### 新增（第十轮：LittleSkin 登录）

- 设置 →「账户」新增 **LittleSkin** 按钮（`Views\Pages\SettingsPage.xaml:107`，AutomationId `AddLittleSkinButton`）：点开是「邮箱/用户名 + 密码」弹窗（`Views\Dialogs\LittleSkinLoginDialog.cs:18`），回车直接提交；登录成功后这个账户自动成为启动账户，弹窗里给「已添加为启动账户，皮肤与头像已保存到本地。」（`SettingsPage.xaml.cs:939-947`）。LittleSkin 是外置皮肤站，用的仍是自己的角色和皮肤，只是身份校验走它的 Yggdrasil 接口（服务器 `https://littleskin.cn`、接口 `…/api/yggdrasil`，`Services\Accounts\LittleSkinAuthService.cs:20-23`）
- 协议按 Yggdrasil 的 authenticate / refresh / profile 走：`LoginLittleSkinAsync`（`Services\Accounts\AccountService.cs:206`）成功后再由 `UpsertLittleSkinAsync`（`:222`）写账户 —— `Type=littleskin`、`Id=littleskin-<uuid>`、`Uuid` 存带横线形式（`:226`/`:241`）、`AccessToken` 用 `SecretProtector` 加密（`:242`）；新增的三个字段 `LittleSkinEmail` / `LittleSkinClientToken` / `LittleSkinServer` 定义在 `Core\Models\AccountRecord.cs:23-29`（clientToken 明文保存，它本身不是访问凭据，刷新令牌时必须有）
- 皮肤和头像一起落到本地：整张皮肤写进 `LauncherData\skins\ls-<uuid>.png`（`AccountService.cs:283`），再从头部 (8,8,8,8) 裁一块放大成 64×64 头像 `ls-face-<uuid>.png`（`:287`、`:315`）。只接受 64×64 或 64×32 的合法 PNG（`:293-302`），不是就忽略并回落到首字母占位；皮肤下载失败只影响外观，不影响登录本身（`:251-264`）
- 启动游戏时令牌按需续期：`IdentityOfAsync` 的 littleskin 分支（`Services\Minecraft\LaunchService.cs:464-474`）先 `EnsureLittleSkinTokenAsync`（没过期直接返回，过期或缺失时用 clientToken 刷新一次，`AccountService.cs:184-199`），然后把明文令牌当 `accessToken`、`userType=mojang`、uuid 去横线交给游戏 —— 版本 JSON 的 `${user_type}` 会替换成 `mojang`（`LaunchService.cs:835`），Minecraft 按 Yggdrasil 校验身份与皮肤
- 错误一律中文，不把英文原文或异常堆栈甩给玩家：密码错误（HTTP 401/403）→「邮箱或密码不正确，请重新输入。」（`LittleSkinAuthService.cs:266`），账号被限制 →「这个账号已被 LittleSkin 暂时限制登录，请到皮肤站查看账号状态。」（`:256`），服务不可用 →「LittleSkin 服务暂时不可用，请稍后再试。」（`:265`）；后端返回「中文。English.」双语时只截取中文段（`:261`、`:271`）

### 修复（第十轮：下载进度合并成一条总进度 / 顶栏去掉 Java 胶囊）

- **下载与启动准备不再分段跳变**：`EnsureInstallAsync`（`Services\Minecraft\LaunchService.cs:484`）改成「先算分母再下载」—— `PlanLibraries` / `PlanClientJar` / `PlanAssetIndex` 把所有待下载文件先列出来（按目标路径去重，`:496-498`），分母是 `plannedBytes + AssetIndex.TotalSize`（`:501-502`）；资源对象要等索引下完才知道，所以下完索引后再按对象实际字节重算分母（`:564-570`），进度条不会先到 100% 再倒退。之后按累计下载字节只报一条 `DownloadProgress`（120ms 节流 + 速度 EMA 平滑，`:510-526`），旧实现「库 55% → 客户端 78% → 资源 100%」的三段跳不再出现
- 下载改成流式写盘：新增 `DownloadItem(Url, Target, Size)`（`:478`）与 `DownloadFileAsync`（`:578-589`，`ResponseHeadersRead` + 81920 缓冲逐块写），已经存在的文件直接按大小计入已完成（`:536-541`）；单个库或资源失败只记一条 `AppLog.Warn("launch", …)`，不会让整次安装停摆（`:553-557`）
- 启动前补文件也走同一条总进度：`LaunchAsync` 把安装百分比映射到 30–58% 阶段（`:105-108`，`30 + percent * 0.28`），「构建参数」从 58% 开始（`:112`），不再按 30 / 44 / 58 三个死点跳
- 浮窗和弹窗显示的是同一份合计：`ProgressCenter.Aggregate()`（`Controls\ProgressCenter.cs:105`）把进行中的任务合成一条 —— 有字节分母就用 `已下载 / 总量`，没有就回落到任务百分比均值，只有「所有任务都没有分母」才标成不确定进度（`:115`）；进度弹窗顶部新增「总进度」卡片（`Views\Dialogs\ProgressDialog.cs:394`，标题「总进度 · N 个任务」`:411`），底部音乐台上方的胶囊也改成「总进度 · N 个任务」（`Views\ShellPage.xaml.cs:1515-1517`）
- **登录弹窗输入框的占位文字回到垂直居中**：`LittleSkinLoginDialog` / `NovalinkLoginDialog` 的用户名框原先套了 `FieldBox` 样式（`Padding=14,10`、`MinHeight=44`）却又写死 `Height=42`，两者打架把「LittleSkin 邮箱或用户名」「密码」顶到输入框上边缘；现在不写死高度，改用 `MinHeight=44` + 上下对称内边距 `Padding=13,11,13,11`（固定高度会把占位文字压在顶部），主题色背景/边框/占位色，见 `Views\Dialogs\LittleSkinLoginDialog.cs:22-45` 与 `NovalinkLoginDialog.cs:20-40`
- **账户下拉菜单补上「LittleSkin 登录」**：与「Microsoft 登录」「NovaLink 登录」并列（`Views\ShellPage.xaml.cs:1238-1245`），点开就是同一个 LittleSkin 弹窗；设置 →「账户」里的 LittleSkin 按钮保留
- **顶栏的 Java 胶囊移除**：`ShellPage.xaml` 的 `JavaChip` / `JavaChipText`、`ShellPage.xaml.cs` 的 `RefreshJavaAsync()` 与 `OnLoaded` 里的调用、`HomePage.xaml.cs` 的 `UpdateJavaChip()` 全部删除（全仓 grep 这三个名字为 0 命中）。顶栏右侧现在只有搜索图标、通知铃与账户头像；Java 版本仍在启动日志第一行输出（`LaunchService.cs:98` 的 `[Lumen] Java <大版本> · <路径>`）

### 调整（第十轮：结束的任务归档成历史日志）

- 游戏实例退出或崩溃后，进度记录不再留在进行中列表：`LaunchDialog` 在 `GamePhase.Exited` / `Crashed` 时调 `ProgressCenter.Finish(..., remove: true)`（`Views\Dialogs\LaunchDialog.cs:72-78`），`Finish` 的 `remove` 参数（`Controls\ProgressCenter.cs:332-336`）先把任务归档、再从 `Tasks` 里删掉（`:351-355`）；下载、安装这类正常结束的任务仍保留「已完成」一行
- 归档目录 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\logs\progress\`：每个任务一个 `<yyyyMMdd-HHmmss>-<任务 id 安全化>.log`（文件名由开始时间与「非字母数字换成 `-`、最多 60 字符」的 id 拼成，`:166-169`），内容是任务名、类型、开始/结束时间、耗时、已下载字节与全部日志行，UTF-8 无 BOM（`:183`）；同目录 `history.jsonl` 追加一行索引，读回时只保留最近 100 条（`:203`、`:130-155`）
- 进度弹窗左侧固定显示「历史日志」小节（没有进行中任务时也在，最多列 10 条，`ProgressDialog.cs:145-155`），点一行用系统默认程序打开对应 `.log`（`:449-484`）—— 关掉启动器之后照样能回看
- 新入口：设置 →「关于」→【进度历史】（AutomationId `SettingsProgressHistory`，`SettingsPage.xaml:732` → `SettingsPage.xaml.cs:1191`），打开的就是这个进度浮层，不必先有进行中的任务
- 模组 / 资源包 / 光影的下载也进同一个进度中心：`ProjectDetailDialog.InstallAsync` 开始时 `ProgressCenter.Start("modrinth:<项目 id>:<文件名>", …)`（`ViewsDialogsProjectDetailDialog.cs:368`），每帧把阶段、百分比、已下载字节与速度写进任务（`:376-390`），依赖安装计入同一任务的「安装依赖 i/n」阶段（`:416-462`），结束时 `Finish` 归档成 `.log` + `history.jsonl`；实测安装 Iris Shaders（2.7 MB，含 Sodium 依赖）在 `logsprogress` 下留下 `20261107-140316-modrinth-…-iris-fabric-1-11-4-mc26-2-jar.log`，历史日志里显示「安装 Iris Shaders · 已完成」

## 1.5.0

> 1.5.0 正式版。这一版先走过测试版通道（`novalink-lumen-preview`）验证，正式版发在 `novalink-lumen-releases`。通道说明见 [docs/PREVIEW.md](docs/PREVIEW.md)。

### 新增

- 插件系统改为实验性功能、默认关闭：**设置 → 实验性 →「启动器插件」**一键开关，切换后横幅提示【立即重启】，重启才生效；关闭时插件不加载、发现页插件商店与设置里的「插件」页都不出现，但插件目录与已安装插件的数据原样保留，重新打开并重启即恢复。临时体验可用一次性参数 `--experimental-plugins` 启动（不写回设置）。安全模式（`--safe-mode` / `--no-plugins` / 环境变量 `NOVALINK_SAFE_MODE`）仍然优先级最高
- 音乐台可拖动进度：悬停浮层里的进度条换成可拖动滑块，拖动时实时跳到对应时间（暂停状态下拖动也会定位），松手后从新位置继续播放；进度显示仍是每 450ms 刷新，拖动时不与自动刷新打架
- 音乐台可调音量：新增音量滑块（0~100%），拖动即时改变播放音量，松手或收起浮层时写回 `lumen.json` 的 `MusicVolume`，重启后沿用
- 快讯图片完整显示并可点击放大：正文配图不再固定 206 高、UniformToFill 裁切，改为按原始比例完整显示（最大高度 240）；点击图片打开全屏查看器（黑底铺满窗口，点【关闭预览】/点空白处/按 Esc 关闭；只支持单张，没有左右切换；加载失败显示「该图片无法显示」）。快讯配图与模组详情截图共用同一个 `ImageViewer`
- 双通道发布与更新：安装包按通道编译（`-p:NovaLinkChannel=Stable|Preview`，未指定时默认 Stable）。测试版只检查 `Eric8844/novalink-lumen-preview`、只接受 `-preview` / `-beta` / `-rc` 标签；正式版只检查 `Eric8844/novalink-lumen-releases`、只接受纯数字标签；不匹配通道的 Release 直接忽略并记一条 `忽略跨通道更新` 日志，不会串台。通道常量在 `NovaLink.Lumen` 与 `NovaLink.Lumen.Services` 两个工程里同时生效（`LauncherChannelInfo` 在 Services 里），预览版序号比较取入口程序集的 `InformationalVersion`；「关于」页与左下版本徽章都显示当前通道（正式版 `1.5.0`，测试版构建 `1.5.0 Preview`）
- 安装包文件名统一带版本号：`NovaLinkLumen-WinUI-Setup-<版本>.exe`，不再产出无版本号的 `NovaLinkLumen-Setup.exe`；两个通道命名规则相同，只是发布到不同仓库
- 版本号 1.5.0：正式版应用内显示 `1.5.0`（测试版构建显示 `1.5.0 Preview`），程序集 / 文件版本 `1.5.0.0`，信息版本与数值版本一致；对外的 User-Agent 与启动参数使用纯数字 `1.5.0`

- 浅色模式全面适配：菜单/下拉里「白底白字」的根因是主题刷在启动时被冻结、切主题时没有刷新（`MenuFlyoutItemForeground`、`ComboBoxForeground` 等一组键），现在切换主题会同步刷新；模组详情弹窗、插件商店卡片、发现页筛选标签、论坛头像与话题卡、库页行卡等原先写死深色值的地方全部改走主题色。顺带修掉模组详情「正在翻译简介…」一直转圈的问题（翻译最多等 4 秒，超时回落英文原文）
- 液态玻璃外观：浮层（弹窗）、顶部搜索/账户/音乐台、各类卡片统一成「磨砂底 + 1px 高光描边 + 顶部一道反光」的液态玻璃；浅色下也不再是一块死白板
- 动效统一与细节过渡：进入/悬停/按下/分批上屏的时长收敛到一套常量（90 / 170 / 230 / 340 / 460 ms 与 32ms 阶梯），列表行改成分批错峰淡入；「内容 / 启动器插件」分段与论坛标签切换补了 220ms 淡入
- 插件可以在启动器里直接删除：「设置 → 插件」每个插件行右侧新增【删除】，二次确认后立即从列表和左侧导航移除；插件正在运行时文件删不掉，会自动排进队列，下次启动前把整个目录挪进回收站（不会留下半个插件），插件自己的数据目录保留
- 插件列表实时跟随文件夹：新增插件目录监听（400ms 防抖），在资源管理器里增删/替换插件后列表与导航自动更新，不用重启；同时修掉「插件写自己的缓存文件也会触发刷新」的误报
- 库页内容也跟随文件夹变化：模组/资源包/世界目录有变动时自动重扫（600ms 防抖）
- 音乐台滑块改自绘外观：轨道、已播放部分与圆形滑块全部自己画（薄荷色圆点 + 浅色下可见的轨道），不再用系统默认滑块；收起时高度与内容一起过渡，不再「先消失进度条、再收起外框」

### 修复

- 切换音乐时整个启动器会顿一下：频谱分析要在切歌时重新建 AudioGraph、打开输出设备、把整首歌重新解码，这些 await 原先全在 UI 线程上续跑。现在建图与销毁旧图都放到线程池，UI 线程只负责换 MediaPlayer 的源；实测切歌期间界面最长阻塞从数百毫秒降到 60ms 以内（含 UIA 往返开销）
- 音乐台悬停浮层不再吞掉鼠标事件：原先浮层设了不可命中测试，展开时导致浮层里的控件全部点不动，这也是此前「悬停后无法控制/拖不动」的根因之一，现已修复
- 音乐台音量曾经永远是 0%：`MediaPlayer.Volume` 在音源尚未就绪时读出来是 0，界面把这个 0 回填到滑块、收起时又写回设置，导致音量被永久清零。现在音量由启动器自己记住，只在读到有效值时才同步界面
- 选择版本弹窗、论坛话题详情在浅色下的可读性问题：弹窗配色改走主题刷（标题/次要文字/行底/选中行/类型标签），论坛「返回话题」改成浅底细边按钮、话题卡不再套两层边框
- 页面右侧滚动条不再压住内容：首页、版本、发现、论坛、库、设置的内容区预留了滚动条宽度
- 卡片「点击缩放」与「悬停放大」打架导致的抽搐：缩放目标改成被记住的状态，进入时只做一次上浮 + 缩放，不再反复互相覆盖
- 顶栏「游戏运行中」状态不再压住搜索框：状态标签与搜索框改为两列布局，窄窗口下也不重叠
- 设置页里的小按钮（删除 / 重命名 / 重命名账户等）以前被压到 46×30、字号 11.5，两个汉字挤成一团；现在统一加宽到最小 64、字号 12.5、高度 32，浅色下也看得清
- 「上次启动 / 上次游玩」记录沿用 1.4.1 的修复（游戏退出时写入时间与时长、页面自动重扫），本版无额外改动，详见 1.4.1「修复」段

### 新增（第五轮：滑动胶囊指示器）

- 分段栏与左侧导航的选中态不再给按钮自己换底色，而是底下有一枚胶囊从旧位置滑到新位置：位移 300–390ms（`ms = 300 + min(90, (|dx|+|dy|) * 0.35)`）、`cubic-bezier(.16,1,.3,1)` 减速；途中沿运动方向拉伸到 1.12、垂直方向压到 0.90，再用弹簧曲线 `(.2,.9,.25,1.22)` 在 `ms + 90ms` 归位。覆盖发现页「内容 / 启动器插件」与类型栏、论坛「话题 / 动态」、库页四个页签，以及左侧导航（竖直滑动）
- 胶囊坐标改为相对宿主 `Canvas` 的 `TransformToVisual` 现量：分段栏放在横向 ScrollViewer 里滚动、窗口缩放后都不会错位（旧实现量窗口坐标，一滚就飘）；胶囊插在底槽与按钮之间，按钮自己始终透明，悬停状态也不会把它盖住
- 深色 ↔ 浅色主题来回切换时胶囊底色跟着重刷：分段胶囊的底色是 `CapsuleBrush()` 现算的新画刷，不是被原地改色的资源，主题事件里由内部按弱引用登记逐个 `Refresh`

### 修复（第六轮：音乐台遮挡与背景律动）

- **音乐台盖住页面最后一行**（发现页「已显示 18 / 78,227」被压在浮层下面，滚到底也看不全）：新增 `src\NovaLink.Lumen\Controls\PageInset.cs`，页面根容器调一次 `PageInset.Hook(root)`，之后监听 `ShellPage.DockedContentInsetChanged`，把根容器底边 margin 加上 `DockedContentInset`（音乐台可见时 = 它的 `ActualHeight` + 30，没有曲目时为 0）。`ShellPage` 在 `UpdateMusicDock()`（`Views\ShellPage.xaml.cs:598`）切显隐后广播（`:603`），并对 `MusicDock.SizeChanged`（`:76`）补发一次——首次显示时 `ActualHeight` 还是 0，少这一次留白会偏小。已接六个页面：发现 / 论坛 / 首页 / 设置 / 版本页传 `PageRoot`，库页传 `LibraryRoot`。（**该机制已在 2026-11-07 第八轮整体移除**：音乐台改为独立浮动胶囊，`PageInset.cs` 与 `DockedContentInset` 全部删除，见下方「调整（第八轮：音乐台改为独立浮动胶囊）」。）
- **没播音乐时深色方块地形还在蠕动**：地形的时间位移原来只跟时间 `t` 走，与播放状态无关。`AuroraBackdrop` 增加 `_musicActive`（`:51`），每帧按 `1 - exp(-4Δ)` 逼近播放状态（`:257`）；`DrawWorld`（`:721`）与 `DrawTerrain`（`:794`）逐层接收，`:798` 起 `motion = 0.12 + 0.88 * musicActive`，漂移 `:799` 与中心摆动 `:810` 都乘这个系数——暂停时只剩 12% 的微动，播放才恢复到完整起伏。实测库页光影空态区（x430-1200 / y330-640，3px 采样）：暂停时两帧 `mean 1.03`、`changedPct 1.96`，播放时 `mean 15.12`、`changedPct 38.68`。
- **点音乐台按钮会让界面重新加载**（出现「正在加载首页…」并重新扫盘）：根因是 `PersistMusicState`（`:568`）/ `PersistMusicVolume`（`:906`）/ `MusicMode_Click`（`:642`）都调 `Settings.Save()`，而 `SettingsService.Save()`（`src\NovaLink.Lumen.Services\Storage\SettingsService.cs:56`）默认写完就广播 `Changed`，首页与论坛页收到就整页 `ReloadAsync`。改为 `Save(notify: false)`（`Views\ShellPage.xaml.cs:579`/`:646`/`:914`，`:52-55` 注释已写明这类字段只写盘不广播）：播放 / 暂停、上一首 / 下一首、播放方式、音量都只落盘；设置页、插件开关等其它位置仍按原样广播。

### 修复（第七轮：原版列表玻璃 / 音乐台遮挡 / 骨架屏 / 加载更多 / 设置页惯性）

- **原版游戏列表的版本行没有玻璃**：`BuildVanillaRow`（`src\NovaLink.Lumen\Views\Pages\DiscoverPage.Vanilla.cs:244-253`）原来自己写死 `Background` / `BorderBrush`，而背景层的玻璃收集器只认样式引用 —— `GlassPanes.Scan()` 用 `ReferenceEquals(border.Style, GlassStyle("GlassRow"))` 判断（`Controls\GlassPanes.cs:87-108`），手写的画刷进不了 `_cards`，整列版本行就一直是「没铺玻璃的普通卡片」。现在改挂 `GlassRow` 样式（`Styles\Theme.xaml:165-170`：`BrushGlassFaint` 底 + `BrushBorderSubtle` 1px 描边 + 圆角 14），每行是一条独立玻璃带，滚动时和库页行卡一样跟着透镜走。
- **内容被音乐台挡住的根因换了一种修法**：上一轮是给页面根容器加底边 `Margin`，等于把整个页面视口收窄，内容硬截在音乐台上方。现在 `Controls\PageInset.cs` 改成**只给页面主滚动区补底部 `Padding`**：`Hook(root)` 用广度优先找页面里面积最大的 `ScrollViewer`（`FindScroll`，`:67-89` —— 设置页里账号列表、数据目录这些小的滚动区不会被误选），把 `Padding.Bottom` 设为「原值 + `ShellPage.DockedContentInset`」（音乐台可见时 = 高度 + 30，没有曲目时为 0）；值没变超过 0.5 就不写（`:35`）。视口仍然到底，内容能滚到半透明音乐台（`MusicDock` 背景是 `BrushSearchGlass`，`Views\ShellPage.xaml:223-226`）底下再露出来，滚到尽头时最后一行停在当时预留的空白上（**第八轮已取消该留白**，现在允许被胶囊压住）。`Loaded` 订阅 `DockedContentInsetChanged`、`Unloaded` 退订，`root.SizeChanged` 补一次重试（`:41-58`）——Hook 时布局还没跑完、滚动区可能还没量出尺寸。（**第八轮已整体删除 `PageInset`**，改为音乐台独立浮动胶囊、内容穿到窗口底，见下方「调整」；此处仅作历史记录。）
- **发现页加载时整页只有骨架卡，没有转圈**：`ShowSkeleton`（`Views\Pages\DiscoverPage.xaml.cs:403-420`）按最终排版铺占位 —— 模组 / 资源包 / 光影是两行三列卡片（`BuildSkeletonRow`，行高 228，`:422-433`），原版游戏是一整张版本列表骨架（`Skeleton.VersionList()`，3 组 × 3 行，`Controls\Skeleton.cs:145-164`）；统计信息、结果面板、空状态一次性切好，数据到位时不跳版。`HoldSkeletonAsync`（`:435-442`）保证骨架至少显示 480ms，避免闪一下。页面正中那层 `LoadingState` 不再用于这两条加载路径（内容 / 原版列表只在收尾时 `HideBusy()`）；插件商店页签仍会调 `ShowBusy("正在读取插件商店…")`（`Views\Pages\DiscoverPage.PluginStore.cs:168`）。
- **点「加载更多」会自动滚回顶部**：`RenderItemsAsync(items, ct, startIndex = 0)`（`:448-476`）新增起始下标，只有 `from == 0` 才 `ResultsPanel.Children.Clear()`（`:452`），追加时从 `from / 3` 那一行接着建、已有行原位替换（`:457-470`）；`LoadMore_Click` 传入 `startIndex = _items.Count`（`:346`、`:364`）。此前每次 `Clear()` 让内容高度瞬间归零，`ScrollViewer` 的偏移被夹回 0，所以看起来像被「踢」回顶部。等待响应期间先在列表末尾插一行骨架（`:348-349`），成功 / 失败都把它移除（`:363`、`:369`）。
- **设置页滚动比别的页面「涩」**：`Views\Pages\SettingsPage.xaml:82` 去掉了 `IsScrollInertiaEnabled="False"`，同时删掉 `ViewChanging` / `ViewChanged` 两个事件与对应处理函数（全仓已无引用），滚动与发现页、库页一致，走 `PageScroll` 样式（`Styles\Theme.xaml:229-234`）的默认惯性。

### 调整（第八轮：音乐台改为独立浮动胶囊）

- **音乐台不再为页面预留底部空间**：用户明确要求「让音乐台成为一个单独的胶囊，所有内容都可以穿过、像上方的搜索栏一样」，并确认取舍是「内容滚到窗口最底、直接从胶囊后面穿过去（到底不留空白）」。现在 `MusicDock` 只是一个浮在内容之上的独立胶囊：未到底时，内容从它半透明的底色（`BrushSearchGlass`，`Views\ShellPage.xaml:223-226`）下面穿过；滚到窗口最底时，最后一行会一直滚到窗口底部、允许被胶囊压住，不再在胶囊上方额外留白。这是有意的取舍——优先「内容贯穿整个窗口」的观感，不再为页脚可读性让位。
- **页脚避让机制整体移除**：`src\NovaLink.Lumen\Controls\PageInset.cs` 文件删除；`DiscoverPage.xaml.cs` / `ForumPage.xaml.cs` / `HomePage.xaml.cs` / `LibraryPage.xaml.cs` / `SettingsPage.xaml.cs` / `VersionsPage.xaml.cs` 中 6 处 `PageInset.Hook(...)` 调用删除；`Views\ShellPage.xaml.cs` 删除 `DockedContentInset` 属性、`DockedContentInsetChanged` 事件、`MusicDock.SizeChanged` 订阅，以及 `UpdateMusicDock()` 里的广播 —— 它现在只负责按有无曲目切显隐（`:587` / `:590`）。全仓 grep `PageInset` / `DockedContentInset` 为 0。
- 影响：第七轮「底部 Padding」的两条记录随本次调整作废（历史段落保留，仅作记录）；音乐台的背景律动、悬停放大、拖动进度与音量调节等行为不变。

### 修复（第九轮：加载更多让开胶囊 / 暂停后背景不再律动）

- **「加载更多」不再被音乐台胶囊挡住**：音乐台固定浮在视口底部正中，而这条按钮原来也在页脚居中，滚到底就正好叠在一起。现在发现页页脚改成四列网格 —— 左边「已显示 N / 总数」，右边依次是「加载更多」与来源说明（`Views\Pages\DiscoverPage.xaml:154-180`），按钮 `Grid.Column="2"` 靠右并留 18px 间距；插件商店页签的「加载更多」（同文件 `:252-254`）同样从居中改为 `HorizontalAlignment="Right"`。底部正中留给胶囊，可点元素一律避开。
- **上次暂停音乐，这次启动没声音但背景方块在律动**：`PlayCurrent(autoplay)`（`src\NovaLink.Lumen\Controls\BackgroundMusicService.cs:281`）原来无论播不播都会 `StartAnalyzerAsync` —— 频谱分析器是**另一条独立的 AudioGraph 在播放同一个文件**，输出增益置 0 所以听不见（`:360`），但解码与取样照旧进行，28 段频谱照样喂给背景地形（`AuroraBackdrop.SetMusicSpectrum` → `DrawTerrain` 的 `band` 抬升），于是出现「静音但有律动」。现在只有 `autoplay` 为真才建分析器，恢复上次暂停的曲目时改调 `StopAnalyzer()`，没有声音就没有频谱；点播放时 `Toggle()` 会走 `ResumeAnalyzer()` 重新建图，悬停、进度、音量行为不变。
- 顺带把透镜诊断日志（`Controls\GlassPanes.cs` 的「透镜扫描」「透镜姿态命中」「透镜列表」）统一收进 `NOVALINK_DEBUG_MOTION=1` 开关：滚动时扫描签名每秒会变两三次、悬停时姿态命中来回翻，常开会把 `lumen-*.log` 刷到几百 KB。排查时带该环境变量启动即可恢复全部诊断输出。

### 新增（v1.5.1：账户皮肤）

- 设置 →「账户」每个可换肤的账户行都有入口：离线账户显示【皮肤】，Microsoft 账户显示【上传到正版】；点开是「账户皮肤」弹窗（`Views\Dialogs\SkinDialog.cs`，`SkinDialog.ShowAsync(XamlRoot, accountId)` 返回 bool 表示有没有改动）。弹窗顶部是 64×64 实时预览，下面是「皮肤模型：经典 / 纤细」、`选择本地图片并上传…`、`换回账号当前皮肤` 与 取消 / 完成
- 离线账户两种取皮肤方式：「用正版玩家的皮肤」输入正版用户名，从 Mojang 会话服务取回他当前的皮肤（`Services\Accounts\MojangSkinService.cs` 的 `FetchSkinAsync`：`api.mojang.com/users/profiles/minecraft/{name}` → `sessionserver.mojang.com` 的 textures → 下载并校验 PNG 尺寸），或者直接选本地 PNG；下载/选用成功后写成本地皮肤，头像与 3D 人物立刻换装
- 正版账户上传到 Mojang：`UploadAsync` 走 `PUT https://api.minecraftservices.com/minecraft/profile/skins`（multipart：`variant` + `file`），令牌在 2 分钟内过期就先刷新；服务端回 401 时抛 `SkinAuthExpiredException`，弹窗捕获后再刷新令牌重试一次（`SkinDialog.cs:297-303`）。403 / 400 / 429 都翻成人话（没有权限、图片不合规、请求过于频繁），不把英文原文甩给玩家
- 皮肤落到本地并成为头像来源：`AccountService.ApplyLocalSkinAsync` 写 `LauncherData\skins\sk-<id>.png` 与 `face-<id>.png`（头部 8×8 裁出来放大到 64×64），`ToPublic` 生成账户信息时优先用本地头像（`LocalFaceFor`）；`ApplyMojangSkinAsync` 按正版用户名拉当前皮肤，`ClearSkin` 清空回默认。头像校验放宽到 `height >= 32`，64×32 的老皮肤也能用
- 真实请求只做了错误分支验证：上传成功会直接改账号皮肤，本轮没有用真实账号做过

### 新增（v1.5.1：插件液态玻璃与主题 API）

- 插件也能用宿主同款玻璃：`context.Ui` 新增 `CreateGlassCard(content, title)`、`CreateGlassPanel(content)`、`CreateGlassButton(text, onClick)` 与 `ApplyGlassCard(element)`（`Plugins\PluginUiService.cs:96-260`）。造出来的卡片是宿主 `GlassCard` / `GlassRow` 样式，圆角、半透明底、1px 描边、边缘折射与悬停高光全部由启动器负责；插件在非 UI 线程调用会返回 null 并写一条日志，不会把界面搞崩
- 新增主题接口 `IPluginTheme`（`Plugins\PluginThemeBridge.cs`）：`IsLight`、`Name` 与 13 个颜色键 —— Accent / Text / TextSecondary / TextTertiary / Surface / SurfaceRaised / GlassCard / GlassBorder / Field / FieldBorder / FillSubtle / Danger / Warn，值都是 `#AARRGGBB`。启动器切深浅主题时先整体重算颜色再逐个回调订阅者，某个插件回调抛异常只记日志、不影响其他插件
- 宿主把主题桥接注入运行时：`LumenRuntime.cs:404` 的 `pluginOptions.Theme = PluginThemeBridge.Instance`，`PluginContext` / `PluginRuntimeOptions` 各加一个可空的 `Theme` 属性，老插件不读它就不受影响
- `docs/plugin-template` 示例重写成玻璃示范页：两张 XAML 卡片走 `ApplyGlassCard`，再现场造一张宿主玻璃卡与一枚胶囊按钮，页面上写着当前主题名与该主题的强调色，切深浅主题立即变配方

### 新增（v1.5.1：启动器内插件文档）

- 设置 →「插件」→【插件文档】打开两档页签：【使用文档】给玩家（怎么装、怎么用、怎么关），【开发文档】给作者（三类插件、清单字段、生命周期、UI 注入、玻璃与主题 API、打包上架）。正文是 Markdown 渲染：标题 / 段落 / 提示块 / 列表 / 表格降级 / 代码块独立专栏（语言标签 + 复制按钮 + 横向滚动），正文可以整段选中复制
- 文档随程序走：`docs/PLUGINS.md` 由 `NovaLink.Lumen.csproj:66-72` 的 `EmbeddedResource` 直接嵌成 `Assets\docs\development.md`，`Assets\docs\usage.md` 同法嵌入；`Views\Dialogs\DocsContent.cs` 负责读内嵌资源并缓存。顺带修掉资源名漏写 `.md` 时「文档没有随程序打包」的报错

### 调整（v1.5.1：版本号与归档）

- 版本号统一到 1.5.1：应用内左下角与「关于」页显示 `1.5.1`，程序集 `1.5.1.0`，安装包文件名 `NovaLinkLumen-WinUI-Setup-1.5.1.exe`；17 处代码与文档由 `scripts/bump-version.ps1 -To 1.5.1` 一次改写，没有手改
- v1.5.0 的源码与产物已冻结进 `archives/1.5.0/`：安装包 `NovaLinkLumen-WinUI-Setup-1.5.0.exe`（69,297,059 B，sha256 `3818225B…02E5`）、发布目录快照与源码 tar 一并存放，`MANIFEST.json` 与 `SHA256SUMS` 进 git 做台账；安装器升级时把用户数据备份到 `Data_Backup_v1.5.0`，卸载程序不会删它

## 1.4.1

数据防线与发版基础设施：统一版本号、双资产发布、升级前备份与安装器卸载保护；首个可用的全类型插件系统。

### 新增

- 升级前自动把启动器数据备份到 `%LOCALAPPDATA%\NovaLink Lumen\Data_Backup_v<源版本>`，`BACKUP-INFO.json` 为完成标记；备份目录固定在 `LauncherData` 之外，卸载删数据不会连带删备份
- `NovaLink.Lumen.exe --restore-data-backup` 恢复入口：先把当前数据改名成 `LauncherData.rollback-<时间戳>` 再回滚，不删任何东西
- `scripts/archive-release.ps1`：安装包 + 源码快照 + 发布目录快照进 `archives/<版本>/` 离线归档，`MANIFEST.json` 与 `SHA256SUMS` 入库做台账
- `scripts/bump-version.ps1`：按白名单逐条改写版本号，任一处与期望旧版本不符立即中止
- `scripts/build.ps1`：publish 之前加“上一版本已归档”闸门，打完包再核对安装包产品版本
- 安装器：卸载时二次确认是否同时删除数据备份（默认保留）、`RMDir /r` 路径守卫、安装前结束正在运行的启动器、安装时对现有数据做一份快照
- 全类型插件系统：`plugins/` 目录扫描、`plugin.json` 清单校验（id 必须等于目录名、`entry`/`scripts`/`resources` 路径不得出插件目录）、每插件独立 `AssemblyLoadContext`（共享程序集回落宿主、`lib\` 放私有依赖、`native\` 探测原生库）
- 类型 A 编译型插件（C# + WinUI）：左侧导航栏注入入口，页面支持编译型 XAML（宿主自动加载插件 `resources.pri`）与纯代码 UI；改插件后需重启启动器，不支持热卸载
- 类型 B 脚本插件（免编译 JavaScript，内嵌 Jint）：`lumen.log / settings / hooks / events` 四个 API 面，提供下载地址改写、文本替换、通知过滤钩子与启动/退出/下载前后事件；沙箱禁用 .NET 互操作、文件、网络与 `eval`，单次执行 2 秒超时、32MB 内存上限
- 类型 C 原生依赖插件：v1.4.1 默认拒载，`kind` 只接受 `assembly` / `script`
- 安全模式：`--safe-mode`、`--no-plugins`、环境变量 `NOVALINK_SAFE_MODE` 可不加载任何插件启动；启动看门狗 `plugin-boot.json` 连续两次异常启动后自动进入安全模式，单个插件连续 3 次加载失败自动隔离（设置页可重新启用）
- `--dev-mode` 开发者模式（还可设环境变量 `NOVALINK_DEV_MODE=1`）：把插件发现、脚本条目、注册钩子数等加载细节写进日志；v1.4.1 只加日志，不放宽任何沙箱或安全限制
- 设置页新增「插件」卡片：插件列表与启停开关、安全模式横幅与重新启用、打开插件目录、刷新、开发模式开关
- 一键安装插件：设置 → 插件 →「安装插件包…」选 `.lumenplugin`（zip），自动解压校验后放进 `plugins/<id>`；已装同名插件时拒绝覆盖，文件过大、目录穿越、清单不合法都会给中文原因而不是异常
- 发现页新增「启动器插件」商店：按 topic `novalink-lumen-plugin` 扫描 GitHub 上的开源插件仓库，支持搜索、按 star / 最近更新 / 名称排序、一键安装与更新提示；结果缓存 30 分钟，GitHub 接口限流时给出中文提示
- 页面加载不再卡住：版本实例 / 首页 / 发现 / 插件商店 / 论坛 / 库在读取数据时显示统一的加载动画（转圈 + 骨架条 + 中文说明），扫描磁盘、请求网络与构建列表都放到后台线程并分批上屏，加载过程中背景动画与导航仍然流畅
- 开发者工具：`--dev-mode` 开发模式下可在设置里选插件源码目录，一键打包成 `.lumenplugin` 并安装（有 `build.ps1` 就走脚本，否则 `dotnet build` 后按清单打包）；插件正在运行时不会因为 DLL 被占用而失败——新包排进 `plugin-updates\`，重启后由新进程在加载插件之前整体替换旧目录（旧目录改名进 `plugin-trash\` 再清理）
- 音乐台：鼠标悬停时面板放大到 1.05 倍（180ms 缓动），并在控制条**上方**展开播放进度条与详细信息（当前时间 / 总时长、第几首共几首、播放方式、音量），控制按钮位置不动；移开鼠标恢复原状。进度每 450ms 刷新一次，暂停时停在当前位置
- 插件安全边界：插件只能拿到账户的显示名、UUID 与头像，拿不到任何令牌；`lumen.json` 里的账户令牌对插件只是 `enc:` 密文，插件直接调用解密 API 会被信任边界拒绝（`UnauthorizedAccessException`），错误只影响该插件。插件与启动器同进程，这层保护是尽力而为，请只安装可信来源的插件

### 修复

- 版本号统一到 1.4.1：csproj、`installer.nsi`、`UpdateService`、`HttpService`、`LaunchService`、侧栏 / 启动画面 / 设置页
- 快讯详情里的「在浏览器打开」是死按钮：新版 feed 不再提供 `readMoreLink`，旧实现却无条件显示按钮、点下去只弹「无法打开」。现在只有合法链接（绝对 `http`/`https`）才显示按钮并唤起浏览器；唤起失败改成弹窗内一行可复制的链接提示，不再叠第二个对话框。没有链接时**完全隐藏**按钮，改为站内直接读正文：按条目 `contentPath` 拉官方 JSON 里的 `body` 转纯文本（按 id 缓存），读取失败则保留摘要并提示稍后再试
- 自动更新收不到安装包：v1.4.1 起每次发布同时上传 NovaLinkLumen-Setup.exe 与 NovaLinkLumen-WinUI-Setup-1.4.1.exe 两个资产
- 「上次启动」永远是「尚未启动」：InstanceMeta 的 LastPlayed / PlaytimeMs 只有读取方、没有任何写入方，所以版本页「最后启动」和首页「上次游玩 / 继续旅程」一直显示未启动，实例列表也无法真正按最近启动排序。现在游戏进程成功起来之后写入本次启动时间，进程退出时累加本次游玩时长（单次封顶 24 小时，防休眠与时钟跳变）；启动失败（缺 Java、类路径坏）不算一次游玩。游戏退出后当前页面会自动重新扫描，时间、排序与「上次游玩 X 分钟前」立即更新。注意：这次修复之后首次启动游戏才会开始有记录，之前的历史无法补算

## 1.4.0

首个 C# / WinUI 3 原生版本，替代 Electron 预览。

### 新增

- 原生 WinUI 3 Shell、标题栏、侧边导航与页面切换
- Win2D 极光背景、方块地面、玻璃卡片、菲涅尔边缘、镜面高光与色散
- 深色 / 浅色主题、任意主题色、自定义图片 / 视频背景、背景音乐
- 版本实例扫描与继承合并、原版与四类加载器安装
- 自动 Java、启动参数、日志与崩溃诊断
- Modrinth 搜索、中文描述、项目详情与安装
- 论坛、动态、评论、个人主页与 AI 审核状态
- 库管理、整合包导入、GitHub Releases 更新
- 离线 / Microsoft / NovaLink 账户与 Microsoft 绑定
- 卸载程序与开始菜单卸载入口

### 修复

- 最大化窗口帧率
- 图标未随发布目录复制
- 详情页图库预览未全屏
- 卸载入口未创建、卸载命令未加引号
- 安装器覆盖旧版本时保留用户数据
