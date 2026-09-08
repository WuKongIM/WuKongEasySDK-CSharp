# WuKongEasySDK-CSharp

[English](README.md) · [C# 接入文档](https://docs.githubim.com/zh/sdk/easy/csharp/getting-started)

参考 [WuKongEasySDK-JS](https://github.com/WuKongIM/WuKongEasySDK-JS) 实现的 C# 轻量通信 SDK。
支持 WebSocket JSON-RPC 连接认证、在线消息收发、自动 RECVACK、自定义事件、心跳和自动重连。
UI、本地存储、会话未读数、离线同步、媒体、推送与业务回执由应用维护。

## 安装

需要 .NET 8 或更新版本，支持 Windows、Linux、macOS。库目标为 `net8.0`，无第三方运行时依赖。
当前未支持 Unity、.NET Framework 或浏览器 WebAssembly。

[WuKongEasySDK 1.0.0](https://www.nuget.org/packages/WuKongEasySDK/1.0.0)
已发布到 nuget.org，通过以下命令安装：

```bash
dotnet add package WuKongEasySDK --version 1.0.0
```

如需从源码构建，可引用正式包对应的固定 commit：

```bash
git clone https://github.com/WuKongIM/WuKongEasySDK-CSharp.git
git -C WuKongEasySDK-CSharp checkout 02ea7d60cd94feef1996f41bca35ffc3b8e18ea6
dotnet new console -n MyChat
dotnet add MyChat/MyChat.csproj reference WuKongEasySDK-CSharp/src/WuKongEasySDK/WuKongEasySDK.csproj
```

也可从固定源码生成本地 NuGet 包：

```bash
cd WuKongEasySDK-CSharp
dotnet pack src/WuKongEasySDK -c Release -o artifacts
dotnet add ../MyChat/MyChat.csproj package WuKongEasySDK --version 1.0.0 --source ./artifacts
```

本地 `.nupkg` 不等于公共 NuGet 发布。

## 最小接入

登录业务系统后，由受信业务后端返回 `uid`、`token` 和 WebSocket 地址。默认设备类型为
PC/Desktop `2`，Token 必须按相同的 `device_flag` 签发。移动端 APP 为 `0`，Web 为 `1`。
客户端不持有 Product HTTP 管理凭据，生产环境使用 WSS。

```csharp
using WuKongEasySDK;

await using var im = WKIM.Init(
    Environment.GetEnvironmentVariable("WUKONGIM_WS_URL")!,
    new AuthOptions
    {
        Uid = Environment.GetEnvironmentVariable("WUKONGIM_UID")!,
        Token = Environment.GetEnvironmentVariable("WUKONGIM_TOKEN")!,
        DeviceFlag = DeviceFlag.Desktop
    });

Action<RecvMessage> onMessage = message =>
{
    // 按 MessageId 去重后，将 Payload 交给应用状态；不要记录完整消息。
    Console.WriteLine("收到消息");
};
im.Message += onMessage;
im.Error += _ => Console.WriteLine("SDK 操作失败");
im.CustomEvent += notification =>
{
    // 使用 notification.Type 和 notification.Data 分发业务事件。
};

await im.ConnectAsync();
var result = await im.SendAsync("bob", ChannelType.Person,
    new { type = 1, content = "你好，C# 👋" });
if (!result.IsSuccess)
    Console.WriteLine($"发送被拒绝：{(int)result.ReasonCode}");

im.Message -= onMessage;
await im.DisconnectAsync();
// await using 最终停止连接与重连，并等待已入队回调结束。
```

`ReasonCode.Success` 为 **1**。必须检查 `SendResult.IsSuccess`，包括 `128–255` 的业务拒绝码；
JSON-RPC 错误会抛出带数字 `Code` 的 `WKIMRpcException`。SEND 成功不代表对端收到或用户已读。

## 生命周期与并发

- 每个 `WKIM` 实例独立，无隐式全局单例。切换账号或更新 Token 时释放旧实例再创建新实例。
- 并发 `ConnectAsync` 共享连接尝试；单个调用者取消只取消自己的等待，`DisconnectAsync` 才停止共享连接。
- 每次连接的 WebSocket 建连与认证共用 10 秒预算。初次失败直接返回；已连接后网络断开默认最多重试 5 次，退避为 1、2、4、8、16 秒，成功后重置。
- 认证拒绝、服务端 `disconnect` / 踢下线、主动断开和释放均停止自动重连。
- `DisconnectAsync` 等待连接任务退出后，才可再次调用 `ConnectAsync`。使用 `await using` 或 `DisposeAsync` 完成最终释放。
- C# 事件使用 `+=` / `-=`，在后台按顺序派发。UI 更新需切回 UI 线程，回调应尽快返回，不能阻塞等待 SDK 异步操作或在回调内等待释放。
- 单个监听器抛异常不会中断其他监听器和自动 ACK。RECVACK 表示 SDK 已接收并入队，不表示业务处理或持久化成功。
- 已入队回调可能在移除监听或断开后继续执行；应用生命周期代码等待 `DisposeAsync` 后，回调才全部结束。
- 不缓存离线 SEND，不自动重发。请求超时、取消或断线时，发送结果可能不确定；按业务策略核对状态，需要重试同一逻辑消息时复用 `SendOptions.ClientMsgNo`。

## 配置与数据

`WKIMOptions` 默认请求超时 15 秒，心跳间隔 25 秒、Pong 超时 10 秒；并发请求上限 256、
待派发事件上限 128、完整 JSON-RPC 帧上限 1 MiB。超过请求上限会立即抛出
`WKIMBackpressureException`；事件队列满会关闭连接，不确认未能入队的消息。
`DebugLogger` 默认为 `null`，启用后也只接收固定运行状态文本。

`SendOptions` 支持 `ClientMsgNo`、`Header`、`Setting` 和 `Topic`。默认 `Header.RedDot = true`，
显式传入的 Header 会保留原值。Payload 接受 JSON 对象或数组，保留业务属性名，以 UTF-8 JSON
的 Base64 发出。接收支持对象与 Base64 JSON；无法解码的字符串按原值保留。

消息 `Payload` 和事件 `Data` 为可在回调后继续使用的 `JsonElement`。消息 ID 为字符串，数字
ID 不经过浮点转换；消息序号和节点 ID 为 `ulong`。消息时间戳单位为 Unix **秒**，自定义事件
时间戳为 Unix **毫秒**。订阅、流发布和历史接口不属于当前轻量 SDK。

消息与事件模型的 `ToString()` 会隐藏内容。应用自己序列化、打印事件或回调参数时仍需避免
泄露 Token、Payload 和完整协议数据。

## 示例与验证

为 Alice/Bob 分别准备桌面设备凭据，在两个终端运行：

```bash
export WUKONGIM_WS_URL=ws://127.0.0.1:5200
export WUKONGIM_UID=alice
export WUKONGIM_TOKEN=alice-development-token
dotnet run --project examples/ConsoleChat -- bob
# 第二个终端用 Bob 自己的 UID/Token，目标参数改为 alice。
```

输入文本发送，`/quit` 或 Ctrl+C 退出。控制台聊天界面会显示消息正文，不输出 Token 或完整协议对象。

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack src/WuKongEasySDK -c Release -o artifacts
WUKONGIM_BINARY=/absolute/path/to/wukongim python3 scripts/smoke.py
```

真实进程测试仅启动并清理自己拥有的回环地址单节点集群，启用 Token 认证、使用 256 Hash Slots。
精确版本与验证范围见 [验证记录](docs/validation.md)。完整 API 对照、参数表与行为边界见
[英文 README](README.md)。协议参考 JS `v2.0.4` / `9c03c98c725982fac224cd1d3b52456eae983975`。

NuGet 发布使用独立的 [发布流程](docs/releasing.md)：三平台验证通过后，使用 GitHub OIDC
临时凭据发布，再从公共源校验包内容并在全新项目中安装验证。
