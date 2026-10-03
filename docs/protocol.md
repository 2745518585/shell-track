# 本地 API v1

下面描述已实现的接口。协议用于本机自动化、CLI 与原生窗口，当前不提供远程访问或浏览器调用。

## 发现与认证

默认数据根目录为 `%LOCALAPPDATA%\ShellTrack`。读取其中的 `connection.json` 获取动态端口与 Host ID，读取 `credentials.json` 获取当前实例的凭据。只访问 `http://127.0.0.1:<port>`，并先调用 `/v1/health` 确认 `hostId` 匹配且 `protocolVersion` 为 `1`。

凭据文件有 `read`、`manage`、`terminal` 三个字段。请求使用 `Authorization: Bearer <token>`。普通查询接受已签发的有效令牌；创建、终止、删除、通知设置与关闭后台只接受 `manage`；终端输入 WebSocket 只接受 `terminal`。管理令牌不能写入终端。

不要把凭据放入 URL、日志或公开文档，也不要将管理凭据传给网页。当前 Host 拒绝含 `Origin` 的请求，并要求 HTTP Host 为 `127.0.0.1`。凭据随后台重启轮换，旧连接信息不能复用。

PowerShell 7 自动化示例，在已有后台的前提下连接：

```powershell
$dataRoot = Join-Path $env:LOCALAPPDATA 'ShellTrack'
$connection = Get-Content (Join-Path $dataRoot 'connection.json') -Raw | ConvertFrom-Json
$credentials = Get-Content (Join-Path $dataRoot 'credentials.json') -Raw | ConvertFrom-Json
$baseUri = 'http://127.0.0.1:' + $connection.port
$readHeaders = @{ Authorization = 'Bearer ' + $credentials.read }
$manageHeaders = @{ Authorization = 'Bearer ' + $credentials.manage }
$health = Invoke-RestMethod "$baseUri/v1/health" -Headers $readHeaders
if ($health.hostId -ne $connection.hostId -or $health.protocolVersion -ne 1) {
    throw 'Host identity or protocol does not match.'
}
Invoke-RestMethod "$baseUri/v1/tasks?skip=0&take=100" -Headers $readHeaders
```

使用自定义数据目录时，自动化程序应读取该目录，而不是默认目录。C# 可以复用 `ShellTrack.Client`；默认连接没有终端输入能力，必须显式 `allowTerminal: true` 才会保留终端凭据。

## HTTP 路由

| 方法与路径 | 功能 | 所需能力 |
|---|---|---|
| `GET /v1/health` | 返回 Host ID、协议版本和应用版本 | 查询 |
| `GET /v1/tasks?skip=0&take=100` | 按启动时间降序返回任务数组 | 查询 |
| `POST /v1/tasks` | 创建任务，或返回同 requestId 的既有任务 | manage |
| `GET /v1/tasks/{id}` | 返回当前任务信息 | 查询 |
| `GET /v1/tasks/{id}/output?offset=0&count=65536` | 读取有界输出页 | 查询 |
| `POST /v1/tasks/{id}/terminate` | 请求终止，已结束任务不重复终止 | manage |
| `POST /v1/tasks/{id}/notification` | 修改运行任务的结束条件与输出正则 | manage |
| `DELETE /v1/tasks/{id}` | 删除已结束任务，成功返回 204 | manage |
| `POST /v1/shutdown` | 返回 202，再关闭后台及其运行任务 | manage |

`skip` 必须非负，`take` 为 1 至 1000；默认 100。当前没有服务端筛选条件或分页总数，客户端逐页查询并按任务 ID 去重。新任务可能改变分页位置，需要一致性快照时可在查询后根据 ID 再同步。

创建请求：

```json
{
  "requestId": "e3ddcc0ffb4f4fb094dd52a72e33ce8a",
  "shell": "pwsh",
  "command": "Write-Output 'hello'; exit 0",
  "workingDirectory": "D:\\work",
  "columns": 120,
  "rows": 30,
  "disconnectPolicy": "Continue",
  "notify": true,
  "notifyPatterns": ["(?i)error|exception", "READY"]
}
```

`shell` 支持 `pwsh`、`powershell`、`cmd`，以及它们的 `.exe` 名称。`command` 和 `rawArguments` 均为 null 时是普通交互式会话。

可选字段 `rawArguments` 为 Windows 原始 shell 参数文本。非 null 时（包含空字符串）进入参数透传模式，不添加默认 shell 选项，也不将其解释为脚本；`command` 必须为 null。空字符串表示直接启动不带参数的 shell；`/D /S /C "echo hello"` 表示传入 cmd 的原始参数。该字段最多 32,000 个字符，不能含空字符。`GET /v1/health` 中 `supportsRawArguments: true` 表示后台支持此模式，旧后台没有该能力。原始参数属于创建请求幂等比较和持久化记录的一部分。目前仍不支持自定义可执行文件或环境变量覆盖。

`requestId` 为客户端创建去重键，必须非空且不超过 128 个字符；建议使用 GUID。重试应保留同一个键和初始创建参数，参数冲突返回 409。初始参数单独保存，窗口修改当前通知开关不会破坏原始请求的幂等重试。工作目录必须存在，命令最长 24,000 个字符，尺寸范围为 1 至 1000。默认 shell 为 `pwsh`，尺寸 120 × 30，断开策略为 `Continue`，通知为 false。

创建成功返回 201 和 `SessionInfo`。启动失败也会留下状态为 `Failed` 的记录；客户端需要检查 `state` 和 `error`，不能仅凭 HTTP 201 判断 shell 已成功启动。

通知设置请求为：

```json
{ "enabled": true, "patterns": ["(?i)error|exception", "READY"] }
```

`enabled` 必须提供，控制结束条件；`patterns` 可省略或为 null，此时保留已有正则，空数组则清空。创建请求的 `notifyPatterns` 默认是空数组，不能为 null。最多 16 个正则，每个最多 2048 字符，空值和无效正则返回 400。返回更新后的 `SessionInfo`。只允许在任务结束前修改，已结束任务返回 409，不补发历史输出。结束条件与多个正则为 OR，每个任务首次命中即触发一次；重复匹配、随后结束或条件修改均不重发。触发后发送失败也不会自动重试。

正则采用 .NET 的 Multiline 与 CultureInvariant 选项，默认区分大小写；`(?i)` 可忽略大小写，`(?s)` 可让点号跨行。输入为去除终端控制序列、处理回车覆盖后的最近文本窗口，最多 65536 字符，支持跨输出片段匹配，不受输出保存配额限制。每个条件一次匹配最多 25ms，超时后停用该条件并记录 `notificationConditionError`，其他条件继续。`GET /v1/health` 的 `supportsNotificationConditions: true` 表示支持此能力；旧后台应升级后再配置。

## 任务信息

JSON 属性使用 camelCase，枚举使用字符串，时间使用 UTC ISO 8601。客户端自行转换为本地时区。

`SessionInfo` 包含：

| 字段 | 含义 |
|---|---|
| `id`、`request` | 任务 ID 与参数；`request.notify` 是当前完成通知开关 |
| `state`、`isFinished` | 生命周期状态及是否结束 |
| `startedAt`、`endedAt` | 开始与结束时间，未结束时 endedAt 为 null |
| `processId`、`exitCode` | shell 进程 ID 与实际退出码；可能为 null |
| `error` | 启动、执行或输出存储问题，正常时为 null |
| `outputLength` | 已产生输出的累计虚拟字节位置 |
| `recordedLength` | 实际保存到文件的字节数 |
| `outputTruncated` | 记录达到上限或发生存储问题，输出不完整 |
| `latestOutputLine` | 最新非空可读输出行，最多 400 字符，较长时截断显示 |
| `columns`、`rows` | 当前实际 ConPTY 尺寸 |
| `notificationStatus`、`notificationError` | 通知阶段与诊断说明 |
| `notification` | 详细 Windows 通知结果，未发送时可为 null |
| `notificationTrigger` | 首次触发记录：kind 为 completed/outputMatch，at 为时间，pattern/matchedText 为正则与匹配摘要 |
| `notificationConditionError` | 条件执行诊断，如正则超时；不影响 shell 的退出码 |

状态为 `Starting`、`Running`、`Stopping`、`Exited`、`Failed`、`Interrupted`。非零退出码的正常 shell 结束仍为 `Exited`。

通知结果包括 `status`、`detail`、`windowsId`、`setting`、`systemState`、`inHistory`。`pending` 在触发前表示等待条件，触发后表示正在发送；`notMatched` 表示任务结束但仅有的输出条件未命中。`submitted` 表示 Windows API 接受，`inHistory` 表示 API 历史查询结果，均不能证明用户已看到弹窗或通知中心条目。桌面通知显示与点击已完成本机用户验收，其他部署方式仍需分别验证。

## 输出分页与缺口

`offset` 非负，不能大于当前 `outputLength`。`count` 范围为 1 至 262,144，默认 65,536。

```json
{
  "taskId": "a5cb79d2340340a19b1b034409bbf8d54",
  "offset": "0",
  "nextOffset": "6",
  "data": "aGVsbG8K",
  "complete": true,
  "truncated": false,
  "gap": false
}
```

`data` 是原始输出字节的 Base64。`offset`、`nextOffset` 使用十进制字符串，避免 JavaScript 整数精度问题。按 `nextOffset` 继续读取；空页且 `complete` 为 false 时稍后重试，官方客户端间隔 40 ms。`complete` 表示任务结束且该页到达最后输出位置。

默认记录上限为 64 MiB。超过上限的实时数据放在每任务 4 MiB 环形缓冲中。若请求位置已不可读，`gap` 为 true，下一位置会跳过缺失区间；这一页数据的实际起点为 `nextOffset - Base64解码后的字节数`，不一定等于请求 offset。不要把缺口伪装为空输出。

后台重启后只保存落盘前缀，环形缓冲丢失，因此旧任务也可能返回 gap。`truncated` 是整条记录是否不完整的标记，`gap` 是这一次读取是否跨过缺失数据。

ConPTY 输出包含 ANSI 控制序列，UTF-8 字符可能跨页。必须使用增量解码器，或把字节直接转发给终端，不要单独对每页调用无状态字符串解码。输出不区分 stdout 与 stderr。

## WebSocket

### `/v1/events`

使用有效查询凭据握手。建立连接后发送当前任务快照，再约每 300 ms 比较并发送变化：

```json
{ "kind": "state", "taskId": "...", "session": { "id": "...", "state": "Running" } }
```

任务被删除时：

```json
{ "kind": "deleted", "taskId": "...", "session": null }
```

该通道传递任务状态快照，不传原始输出；输出使用 HTTP 分页。当前没有事件序号、过滤订阅、持久化事件游标或断线续传，短暂的中间状态可能被合并。重连或发现 Host ID 改变时重新同步任务列表。慢接收方不会阻塞终端排空。

### `/v1/tasks/{id}/terminal`

CLI 专用，使用 `terminal` 凭据。任务必须未结束且没有其他输入所有者；多个只读查看器不占输入所有权。

- 二进制消息：原始输入字节。
- 文本消息：JSON 尺寸调整，例如 `{"kind":"resize","columns":120,"rows":30}`。
- 单条消息最多 65,536 字节；尺寸范围为 1 至 1000。

该 WebSocket 不返回终端输出，输出仍由 HTTP API 读取。连接关闭时按任务 `disconnectPolicy` 决定继续或终止。管理窗口不建立这个连接。

## 错误与一致性

错误 JSON 为 `{"code":"...","message":"...","requestId":"..."}`。其中 requestId 是服务器的请求追踪标识，不是创建参数里的幂等键。

| HTTP 状态 | 常见原因 |
|---|---|
| 400 | 非法目录、尺寸、游标、分页参数或 JSON |
| 401 | 未提供或提供了失效凭据 |
| 403 | 能力不足，或 Origin / Host 被拒绝 |
| 404 | 任务不存在 |
| 409 | 运行数量或记录配额不足、requestId 参数冲突、删除活任务、修改已结束通知、重复输入所有者 |
| 500 | 后台内部错误，可通过前台运行 Host 的日志定位；自动启动后台默认静默 |

创建参数冲突等通常使用 `invalid_request`，内部错误使用 `internal_error`；客户端应同时检查状态码与消息，当前未为每类错误分配独立稳定 code。

列表分页、事件流与输出读取各自独立，当前没有跨端点原子快照。持续输出用字节游标去重；任务状态以最新快照覆盖，恢复连接后重新获取列表。任务完成状态在进程结束、输出排空与元数据提交后发布，读输出仍应继续到 `complete`。
