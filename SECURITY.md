# Security Policy

## 报告安全问题

请不要在公开 Issue 中披露可利用细节、账户数据、令牌或服务器配置。

请通过以下方式私下联系维护者：

- GitHub: https://github.com/Eric8844
- 或在仓库的 Security 页面使用 "Report a vulnerability"

请在报告中包含：

- 受影响的版本或提交
- 复现步骤
- 影响范围
- 你建议的修复方式（如果有）

## 密钥与数据

本仓库只包含桌面启动器源码，不含 NovaLink 服务端实现与运行数据。以下内容不接受包含在 PR 中：

- 管理员密码、SMTP 授权码、AI API Key
- Bearer Token、账户 Cookie、Microsoft 令牌
- 服务器 SSH / SMB / 远程桌面凭据
- 用户上传的图片、视频、聊天记录

如果你在历史提交中发现密钥，请立即：

1. 在对应平台吊销并轮换密钥。
2. 清理 Git 历史（`git filter-repo` 或 BFG）。
3. 通知维护者。

## 支持范围

安全修复会优先应用到当前 `main` 分支与最新 Release。旧版本不保证持续获得修复。
