// NovaLink Lumen 脚本插件示例（类型 B：免编译 JS，Jint 引擎）。
// 完整 API 见 docs/PLUGINS.md 第 5 节。
//
// 注意：
// - 只支持同步代码。没有 async/await、Promise、setTimeout、require、console、fetch。
// - 全局只有 lumen 一个对象；日志请用 lumen.log / info / warn / error。
// - 脚本在启动器启动时同步执行一次；改完 main.js 需要重启启动器。
// - 钩子回调抛异常只会记日志并回落宿主默认行为，不会拖垮启动器。

// 1) 日志：任意值，String() 后写进宿主统一日志（区域 plugin:script-hello）。
lumen.log("script-hello 已加载，版本 " + lumen.plugin.version);
lumen.info("插件数据目录：" + lumen.plugin.dataDir);

// 2) 设置：读上次的启动次数，写回 +1。
//    lumen.settings.get 返回 set 时的原类型；不存在返回 null。
var count = lumen.settings.get("runCount");
count = (typeof count === "number" ? count : 0) + 1;
lumen.settings.set("runCount", count);
lumen.log("这是本插件第 " + count + " 次初始化。");

// 3) 下载地址钩子：把示例官方地址改写成镜像地址。
//    返回对象 = 接管；返回 null = 不接管（宿主继续用原地址）。
//    镜像内容必须与 sha1 一致，否则下载校验会失败。
lumen.hooks.onDownloadRequest(function (req) {
  if (req.purpose === "modloader" && req.url.indexOf("github.com") >= 0) {
    return {
      url: req.url.replace("github.com", "mirror.example.com"),
      displayName: "镜像 A"
    };
  }
  return null;
});

// 4) 文本替换钩子：把展示文本里的英文术语换成中文。
//    返回字符串 = 替换；返回 null / undefined = 不修改。
lumen.hooks.onTextReplace(function (text) {
  if (!text) return null;
  return text.replace(/\bMinecraft\b/g, "我的世界");
});

// 5) 通知过滤钩子：返回 false 表示该通知不展示。
//    null / undefined 会被当成 true（展示）。
lumen.hooks.onNotification(function (title, body) {
  if (title.indexOf("自动检查更新") >= 0) return false;
  return true;
});

// 6) 事件订阅：app.startup / app.shutdown / download.before / download.after。
//    未知事件名会记 warn 并返回 0（不抛异常）。
lumen.events.on("app.startup", function (args) {
  lumen.log("收到 app.startup 事件。");
});

lumen.events.on("download.before", function (args) {
  var url = args && args.url ? args.url : "未知地址";
  lumen.log("即将下载：" + url);
});
