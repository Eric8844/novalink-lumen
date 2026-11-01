# 插件商店上架指南（NovaLink Lumen v1.4.1）

> 一句话：仓库打上 topic `novalink-lumen-plugin`、根目录放一份合法的 `plugin.json`、把 `<id>.lumenplugin` 传到 GitHub Release —— 启动器「发现 → 启动器插件」里就能搜到它。

仓库根目录最少长这样：

```text
你的插件仓库/
├── plugin.json                  # 必需：商店和安装器都读它（就是安装包里的那份清单）
├── store.json                   # 可选：商店展示信息与下载地址（不参与安装校验）
├── README.md                    # 建议写清用法、截图与安全说明
├── src/                         # 源码（编译型插件；脚本插件就是 scripts/）
└── dist/
    └── hello.world.lumenplugin  # 上传到 Release 的安装包（本质是 zip）
```

---

## 一、商店是什么

- 入口：启动器左侧导航「发现」页里的「启动器插件」一栏。
- 数据来源：**GitHub 公开仓库**。启动器用 GitHub 搜索接口按 topic 扫描：
  `https://api.github.com/search/repositories?q=topic:novalink-lumen-plugin`（topic 字面量定义在 `src/NovaLink.Lumen.Services/Plugins/Store/PluginStoreModels.cs:158`）。
- 启动器**不托管、不审核、不签名**插件。商店只做三件事：读仓库根的 `plugin.json`、按约定算出安装包地址、把包交给本地安装器（与「安装插件包…」同一套校验）。
- 安装后的行为与手动安装完全一样：落到 `<数据根>\plugins\<id>`，重启启动器生效。安装与卸载细节见 [PLUGINS.md](PLUGINS.md) 第 2 节。

列表里每一条展示：

| 展示项 | 来源 |
|---|---|
| 名称 / 简介 / 作者 / 版本 | `plugin.json` 的 `name` / `description` / `author` / `version`（作者缺失时回退成 GitHub 仓库所有者） |
| 类型标签（编译型 / 脚本型） | `plugin.json` 的 `kind` |
| star 数、最近更新 | GitHub 仓库元数据 |
| 标签 | `store.json` 的 `tags` |
| 已安装 / 可更新 | 本机 `plugins\<id>\plugin.json` 的版本与商店版本比较 |

排序：**推荐**（GitHub star 数，默认）/ **最近更新** / **名称**（`PluginStoreSort`，`PluginStoreModels.cs:11-21`）。搜索框里的关键词会与 topic 一起提交给 GitHub，匹配仓库名与描述。

---

## 二、5 分钟上架清单

1. **打 topic**：GitHub 仓库页 → 右上齿轮旁「Topics」→ 添加 `novalink-lumen-plugin`；或用 CLI：
   `gh repo edit --add-topic novalink-lumen-plugin`
   （topic 是唯一的可扫描标识，拼错、大小写不同、写在 README 里都不算。）
2. **根目录放 `plugin.json`**：从 `docs/plugin-template/plugin.json` 复制改写，字段含义见第三节与 [PLUGINS.md](PLUGINS.md) 第 4 节。必须是仓库根目录，不能只放在 `dist/` 或 `src/` 里。
3. **做安装包**：`.lumenplugin` 本质是 zip，根目录直接放 `plugin.json` + 程序集 + `.pri`（不要多套一层文件夹）。命名用 `<插件 id>.lumenplugin`，例如 `hello.world.lumenplugin`。
4. **传到 Release**：Releases → Draft a new release → tag 建议 `v<插件版本>` → 把安装包作为资产上传 → 勾选 **Set as the latest release**。默认下载地址依赖 latest：
   `https://github.com/{owner}/{repo}/releases/latest/download/<id>.lumenplugin`
5. **（可选）加 `store.json`**：需要换资产名、用非 GitHub Release 的下载地址、加标签或主页时见第四节。
6. **验证**：打开启动器 →「发现」→「启动器插件」→ 搜你的插件 id 或名称，点安装，重启后插件生效。
7. **发新版**：改 `plugin.json` 的 `version` → 重新打包 → 传一个新 Release 并设为 latest。商店最长 30 分钟后刷新出新版本（见第六节缓存）。

> 仓库的 description **不会**当成插件简介——简介以 `plugin.json` 的 `description` 为准。但仓库描述写清楚有助于 GitHub 搜索命中，建议写。

---

## 三、`plugin.json` 要点

商店读取并用于展示/筛选的字段：

| 字段 | 商店用途 | 缺失/非法的后果 |
|---|---|---|
| `id` | 去重、判断本机是否已装、推断默认安装包名 | 缺失或不合法的仓库**静默跳过**，不出现在商店 |
| `name` | 列表标题 | **必填**：缺失或空字符串的仓库跳过（不会回退成 id） |
| `version` | 与本机已装版本比较，决定是否显示「可更新」 | **必填**：缺失或空的仓库跳过 |
| `author` | 作者名 | 可省略；缺失时回退成 GitHub 仓库所有者 |
| `description` | 简介 | 可省略，简介为空不影响上架 |
| `kind` | 显示「编译型 / 脚本型」标签 | 可省略；商店只负责展示，安装时才要求必须是 `assembly` / `script` |
| `apiVersion` | 兼容性判断（当前只支持 `1`） | 不等于 1 时**条目仍然显示**，但标记为不可安装，原因写“使用的是插件 API 版本 X，当前启动器只支持 1，需要请作者更新” |
| `minLauncher` / `maxLauncher` | 与本机启动器版本比较 | 不满足时**条目仍然显示**，但安装按钮禁用并给中文原因 |

安装与加载才用、商店不展示的字段：`schema`、`entry`、`scripts`、`resources`、`capabilities`、`ui`、`loadContext`、`checksum`。完整字段表与校验规则见 [PLUGINS.md](PLUGINS.md) 第 4 节。

关于可见性：**清单读不出来 = 从商店消失**（不弹错误、不在列表里标红）——具体是：仓库根没有 `plugin.json`、JSON 解析失败、`id` 不合法、`name` 或 `version` 为空。而「能读出来但版本/接口不兼容」的条目**会显示**，只是安装被禁用并说明原因。所以「搜不到」十有八九是清单问题，先照 [PLUGINS.md](PLUGINS.md) 4.4 的校验规则自查，再用第六节的办法确认。

---

## 四、`store.json` 字段表（可选）

放在仓库根目录，只影响商店展示与下载地址，**不参与安装校验**（模型见 `PluginStoreModels.cs:134-152`）。全部字段可省略，一个字段都不写也能上架。

```json
{
  "asset": "hello.world.lumenplugin",
  "download": "",
  "tags": ["界面", "下载源"],
  "homepage": "https://github.com/your-name/your-plugin"
}
```

| 字段 | 类型 | 省略时的默认行为 | 说明 |
|---|---|---|---|
| `asset` | string | 按 `<插件 id>.lumenplugin` 推断 | Release 里的安装包文件名。只有当资产名和 id 不一致时才需要写；名字里的非法字符/超长会让它被忽略并退回默认名，缺少 `.lumenplugin` 后缀会自动补上 |
| `download` | string | 用 `https://github.com/{owner}/{repo}/releases/latest/download/{asset}` | 完整下载地址；**给了就优先**，用于不用 GitHub Release 的托管方式（例如自己的对象存储）。必须是 http/https 绝对地址，其它值会被忽略 |
| `tags` | string[] | 空数组（不显示标签） | 仅展示，方便分类，不影响搜索排序；最多取前 4 个 |
| `homepage` | string | 不显示 | 项目主页 / 文档地址，仅展示 |

注意：
- `store.json` 不合法（坏 JSON、字段类型不对）只会让这些附加信息失效，不会让插件从商店消失——插件本体以 `plugin.json` 为准。
- 换下载地址、改资产名之后，本机可能还留着缓存（搜索结果 30 分钟、仓库清单 6 小时），见第六节。
- 不要在里面写版本号：版本以 `plugin.json` 的 `version` 为准，写两份迟早不一致。

---

## 五、安装包命名与打包

`.lumenplugin` 就是 zip，只是扩展名不同。安装器解压后要求：

- **根目录**直接是 `plugin.json`、入口程序集（`entry.assembly`）、可选的 `.pri`，不能再包一层 `hello.world/` 目录；
- `plugin.json` 合法（见 [PLUGINS.md](PLUGINS.md) 4.4）；
- 包内文件不超过 200 MB / 5000 个；
- 私有依赖放 `lib\`，原生 DLL 放 `native\`（但类型 C 本版拒载，见 [PLUGINS.md](PLUGINS.md) 1.1）。

命名约定：**`<插件 id>.lumenplugin`**。商店的默认下载地址就是按这个名字拼的，改成别的名字就必须在 `store.json` 的 `asset` 里写清楚（`asset` 会先被安全化：只保留字母、数字、`.`、`-`、`_`，长度上限 120，不合格就退回默认名）。

### 方式一：用模板的 `build.ps1`（推荐）

`docs/plugin-template/` 里已有脚本，产物直接是 `dist\<id>.lumenplugin`：

```powershell
# 在插件仓库根目录（复制出来的模板）里执行
.\build.ps1
```

### 方式二：手动 zip 后改名

```powershell
# 1) 准备一个只含 plugin.json + dll + pri 的目录，例如 .\pkg
# 2) 压成 zip
Compress-Archive -Path .\pkg\* -DestinationPath .\dist\hello.world.zip -Force
# 3) 改名成 .lumenplugin
Move-Item .\dist\hello.world.zip .\dist\hello.world.lumenplugin -Force
```

坑：`Compress-Archive` 只认 `.zip`，直接写 `.lumenplugin` 会报错；`.ps1` 要存成 **UTF-8 with BOM**，否则 Windows PowerShell 5.1 会把中文注释读成乱码。

---

## 六、常见问题

**Q：打了 topic 还是搜不到？**
按顺序排查：① topic 拼写是否是 `novalink-lumen-plugin`（全小写、连字符）；② 仓库根目录是否真有 `plugin.json`；③ 清单是否合法（`id` 正则、`apiVersion` 必须是 1、`kind` 只能是 `assembly`/`script`）；④ 是否在启动器里搜的关键词太窄（换成 id 或作者名）；⑤ 商店有缓存（搜索结果 30 分钟、仓库清单 6 小时），重启启动器或等缓存过期再看。清单读不出来时启动器不会报错，只是不显示——所以这一步最容易被漏掉。

**Q：点安装提示「这个文件不是有效的插件包」？**
说明包本身有问题，而不是商店的问题。常见原因：zip 里多套了一层目录（`plugin.json` 不在根）；`plugin.json` 是坏 JSON 或缺少必填字段；包实际是 `.lumenplugin.zip` 这种双扩展名；下载到的是 GitHub 的错误页 HTML（Release 里没传资产，或者 `asset` 名字写错导致 404 被保存成了文件）。用第七节的办法先手动解压看一眼。

**Q：发了新版，商店里还是旧版本？**
两级缓存共同决定：搜索结果缓存 30 分钟（`PluginStoreModels.cs:170` 的 `CacheTtl`），仓库里的 `plugin.json`/`store.json` 缓存 6 小时（`PluginStoreClient.cs:28` 的 `ManifestTtl`）。缓存文件是数据根下的 `plugin-store-cache.json`（`PluginStoreModels.cs:173`）。另外「latest」只指向**最新且被标记为 latest** 的 Release——草稿或预发布不会成为 latest。

**Q：列表顶部提示请求太频繁？**
GitHub 对未登录请求限流（搜索接口大约每分钟 10 次）。超限时列表顶部提示“GitHub 接口暂时限流了（未登录时每分钟最多 10 次搜索），稍等几分钟再刷新。”，并且**回落显示上次缓存**；连不上 GitHub、超时也各有中文提示。装完后不需要一直刷。

**Q：私有仓库 / 不开源的插件能上架吗？**
不能。商店依赖 GitHub 公开搜索与仓库根的公开 `plugin.json`，私有仓库扫不到、清单也读不了。不想开源的话只能让用户手动下载 `.lumenplugin` 后用「安装插件包…」安装（见 [PLUGINS.md](PLUGINS.md) 2.3）。

**Q：一个仓库能放多个插件吗？**
不能。约定是「一个仓库 = 一个插件」，仓库根的 `plugin.json` 就是它唯一的身份。需要发布多个插件就建多个仓库。

**Q：商店里显示「可更新」，但点安装提示已存在？**
覆盖规则与手动安装一致：同名插件目录存在时安装器会拒绝覆盖（[PLUGINS.md](PLUGINS.md) 2.4）。先退出启动器删掉 `plugins\<id>`，或在设置 → 插件里停用后再装。

**Q：仓库 description 为什么没显示成简介？**
约定如此：简介只取 `plugin.json` 的 `description`。仓库描述只用于 GitHub 搜索命中。

---

## 七、安全与免责

- 插件**运行在启动器进程内**，没有沙箱。编译型插件等同于随便执行代码；脚本插件也只在 Jint 层禁用 .NET 互操作、文件与网络，不等于安全边界。
- 因此插件**可以访问启动器能访问的一切**，包括你的游戏账号令牌、实例配置、下载凭据。设置页的安全提示原文就是这么写的：「只安装可信来源的插件。插件运行在启动器进程内，可访问你的游戏账号令牌。」
- 启动器**不审核**仓库内容、不做签名校验、没有白名单。商店里的内容等于「GitHub 上公开的、打了 topic 的仓库」，仅此而已。
- 上架也意味着责任：请在自己的 README 里写清插件做了什么、会读写哪些数据、是否有网络请求；发版前自己在本机完整装一遍。
- 用户侧的兜底：单个插件加载失败只影响它自己；连续失败 3 次会被隔离；连续两次启动异常会进入安全模式（`--safe-mode`）。这些机制救命，但不能当安全沙箱用。
- 发现可疑插件：不要安装，直接停用/删除该插件目录，并在仓库提 issue。恶意插件请同时向 GitHub 举报仓库。

---

## 八、官方推荐列表说明

- **现在没有白名单、没有人工推荐位。** 「推荐」排序就是 GitHub star 数，谁都可以上架，谁都可以被搜到。
- 想让插件更容易被发现：把 README 写清楚（功能、截图、权限与数据说明）、保持 Release 有可用安装包、给仓库加准确描述、把 `tags` 填成用户会搜的词。
- 如果以后引入官方认证/白名单，规则会先写进这份文档再上线；在那之前，任何「官方推荐」的说法都不是启动器的承诺。

### 参考实现：已上架的示例插件

[Eric8844/novalink-lumen-plugin-server-status](https://github.com/Eric8844/novalink-lumen-plugin-server-status) 是第一个按本指南上架的编译型插件（Minecraft 服务器状态查询），可以直接拿来对照：

- 仓库根有 `plugin.json`（`id` = `eric8844.serverstatus`，`minLauncher` = `1.4.1`）和 `store.json`（`asset` + `tags`，`download` 留空，走默认的 Release 约定）；
- 打了 topic `novalink-lumen-plugin`；
- Release `v1.0.0` 的资产名就是 `eric8844.serverstatus.lumenplugin`，和 `store.json` 的 `asset` 一致；
- 从启动器「发现 → 启动器插件」搜「服务器状态」→ 安装 → 重启，左侧导航出现「服务器状态」。

注意它的 `id` **不能**写成 `novalink.` 或 `system.` 开头——那是宿主保留前缀，清单校验会直接拒绝（`PluginManifestReader.cs` 的 `ReservedIdPrefixes`）。

---

相关文档：[PLUGINS.md](PLUGINS.md)（插件安装、清单字段、安全模型与故障排查）、[plugin-template/](plugin-template/)（可直接编译或直接改的模板）。
