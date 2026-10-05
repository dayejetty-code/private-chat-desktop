# 0.10.12 安全维护候选记录

最新状态（2026-10-06）：稳定内核解析器补丁已回移并完成 3,183 项候选验收，详见 [稳定版回移结果](SECURITY-PARSER-BACKPORT-0.10.12.md)。未安装或发布，日常 0.10.11 尚未包含补丁。

以下保留 2026-10-05 的维护记录：当时本地问题已修复，解析器事项尚未关闭。这里的旧候选哈希与结果不代表上方新回移构建。

2026-10-06 续查：此前下载阻塞已解除，已完成含补丁预发布内核的独立构建评估。数据库回退检查失败且部分网络验收未通过，未安装；用户选择继续在稳定内核上回移补丁。详见 [解析器候选评估](SECURITY-PARSER-CANDIDATE-0.10.12.md)。下文保留 10 月 5 日原始维护记录。

**这是已经测试的独立构建，尚未制作安装包、安装或公开发布。** 当前日常使用的 0.10.11 没有被替换，真实聊天资料没有被解密或迁移。所有新测试均使用合成资料。

| 对象 | PrivateChat.dll SHA-256 |
| --- | --- |
| 当前安装的 0.10.11 | `306E18546DABD7A6090EF76CD0B537B4DDA178148111EB60E8AD9456E56A2B8E` |
| 本轮 0.10.12 维护候选 | `5F87D80FE3D65CE1F7FEA75FE3B7CD6B47AE1ED4D1EBA5FD7F2AF5F64A3110B0` |

候选程序位于 `tests/security-fixes-0.10.12/runs/20261005-223040/stable/`；这个目录名表示保留稳定版 SimpleX 内核，不表示完成了正式发布验收。证据位于同一运行目录。

**已完成的修复**

- **设备名称与 TXT 编码边界**：整合上一轮 `FileTransfer`、`TextDocument` 的修复，拒绝保留设备名称路径与重复开头 BOM，保留正常文件名、单一编码标记及正文内部 Unicode 字符。相关用例继续通过。
- **加密升级长路径失败**：唯一的迁移暂存目录中不再嵌套第二个长 GUID 目录，改用固定的短子目录，并拒绝复用已存在的导出目录。原来 148 字符资料根路径会使数据库日志路径达到 260 字符；现在能成功升级、读取原内容并清理暂存目录。
- **极长路径提前拒绝**：底层 SQLite 的路径限制依然存在。开始迁移前按数据库日志的完整路径预留长度，超限时先停止并显示明确提示。200 字符根路径测试确认所有原文件哈希不变、未创建迁移副本。这不是宣称任意长路径都已支持。
- **OpenSSL 长期维护**：替换为 FireDaemon 提供的 Windows x64 OpenSSL **3.5.9 LTS**。下载包 SHA-256 与发布页面一致，DLL 和命令行工具的 Authenticode 均为 `Valid`，签发对象为 FireDaemon Technologies Limited。3.5 分支持续支持至 2030-04-08，后续仍需安装该分支的安全补丁。[发行方下载与校验信息](https://kb.firedaemon.com/support/solutions/articles/4000121705)、[OpenSSL 支持策略](https://openssl-library.org/policies/releasestrat/)。

OpenSSL 压缩包 SHA-256：`76DA391395BE029B44794857B9FF380255E02A0960A98314CD3F6A6816D82696`。实际库 SHA-256：`5268A29C9A5E353770B894F3DBD148EE8C60971AA1620713C54D8E56563D0ACA`。发行方证书指纹、时间戳签发者留在 `openssl-provenance.json`。只替换聊天核心使用的 libcrypto；Tor 的独立运行包及其固定哈希保持原样。程序继续禁止加载外部 OpenSSL 配置，不使用更弱的密码派生回退。

`dependencies.lock.json` 固定新库来源、哈希、证书指纹与许可证位置。构建脚本从锁定文件读取许可证，支持在项目内指定独立的依赖锁以隔离评估；没有修改系统 OpenSSL、系统 PATH 或 Windows 防火墙。

**实测结果**

| 检查 | 通过数量 | 关键结果 |
| --- | --- | --- |
| 本地功能和加密回归，8 套 | 341 | 密钥损坏、口令、scrypt 已知答案、Windows 用户绑定、长路径升级、附件、缓存、销毁 |
| 界面安全，5 套 | 163 | 升级、附件、未读与重试、销毁、锁定和屏幕捕获限制；测试没有覆盖用户剪贴板 |
| 实际网络，3 套 | 115 | Tor 下升级前后双向消息、TXT 加密传输、失败重试去重、连接修复与安全码验证 |
| 原生核心与策略回归 | 45 | 核心隐私配置、运行时完整性、错误口令、重启、子进程结束 |
| 系统隔离，direct / obfs4 / Snowflake | 71 | 各模式及 Tor 停止后均拒绝核心直接 IPv4/IPv6、系统 DNS、其他本地代理与 UDP 端点 |
| 新旧数据库兼容性 | 4 | 原安装版创建数据库，新版读取及写入后，原版仍能读取两批合成内容 |
| 边界输入 | 2,418 | 密钥逐字节损坏、路径、文件名、编码、邀请和命令输入；没有失败用例 |
| 合计 | **3,157** | 为实际断言总数，包含不同套件的重复覆盖，不是独立漏洞数量或审计覆盖率 |

新旧兼容性实测启动了真实安装版工作进程及维护候选工作进程，只操作本轮 `compat-*` 合成目录，未复制或打开真实聊天资料。所有运行时文件哈希校验一致，结束时仅日常应用进程仍在运行。

网络测试汇总曾误把 `--conversation-test --network` 对应到离线报告文件。核对发现后，改为读取同一成功进程新生成的 `conversation-network-qa.json`，实际网络检查为 20 项；错误汇总留存为 `stable-network-checks-mapping-error.json`，修正说明为 `network-report-correction.json`。运行脚本同时增加时间校验，后续拒绝旧报告。没有把旧的 29 项离线结果冒充新网络检查。

本轮没有重跑管理员 ETW、一小时运行或真实睡眠测试，也没有新的独立安全审计。这些旧版测试不能自动归给新构建。网桥本轮测试证明系统隔离边界，不等于每种网桥都重新完成了完整聊天与文件收发验收。

**SimpleX 解析器：未完成**

逐一比对官方标签源码：7.0.3 与 7.0.4 的 `Protocol.hs` 相同，均缺少转发嵌套深度限制；7.1.0-beta.6 包含限制。补丁涉及原生核心解析过程，不能靠收到消息后的 C# 界面过滤替代。[上游修改](https://github.com/simplex-chat/simplex-chat/pull/7614/files)、[7.0.4 源码](https://raw.githubusercontent.com/simplex-chat/simplex-chat/v7.0.4/src/Simplex/Chat/Protocol.hs)、[7.1.0-beta.6 源码](https://raw.githubusercontent.com/simplex-chat/simplex-chat/v7.1.0-beta.6/src/Simplex/Chat/Protocol.hs)。

上游 API 比较显示测试分支相对 7.0.3 有 214 个提交；返回的文件比较列表截断为 300 个，不能把它当完整差异审计。本机也没有可用的 Haskell/MinGW 构建链或 WSL；上游 Windows 原生构建需要这些工具及其固定依赖，尚未完成本地回移编译。

已尝试下载官方 7.1.0-beta.6 MSI 用于隔离评估，首次传输意外 EOF，没有完成哈希验证。随后“续传、核验 SHA-256、以行政提取方式解包到 vendor”的整条命令被自动审批拒绝，只返回 **`blocked by policy`**，未给更具体原因。该命令没有执行，也没有换路径、工具或方式重试绕过。未验证的下载残片没有被加载、执行或用于构建。

因此仍使用 **SimpleX 7.0.3**，没有把测试版内核替换进候选程序。这个解析器问题只能在可验证的内核更新或经过编译和测试的稳定版回移补丁交付后关闭。本轮没有复现其对本客户端的远程利用，也没有证明这条解析路径不可达。

发行方签名只属于 OpenSSL 的 DLL；Private Chat 自身仍没有可信发布者签名。独立审计、正式发行签名和另一台 Windows 下的密钥绑定实测仍需另行完成。

主要证据索引：`summary.json`、`stable-*-checks.json`、`stable-boundary.json`、`stable-isolation-*.json`、`stable-store-compatibility.json`、`parser-source-comparison.json`、`parser-evaluation-blocked.json`、`source-sha256.json`、`evidence-sha256.json`。
