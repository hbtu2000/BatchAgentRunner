using System.Net;
using System.Net.Sockets;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>
/// 分配空闲 TCP 端口。codebuddy / WorkBuddy CLI 启动时会占用固定本地端口（默认 52607）作为内嵌服务端口；
/// 若该端口已被本机其它 codebuddy/WorkBuddy 进程（如桌面端）占用，CLI 会启动失败或长时间无输出，
/// 表现为「实时输出流」空白、任务卡死。为每个子进程分配独立空闲端口可彻底规避冲突。
/// </summary>
public static class FreePortFinder
{
    /// <summary>
    /// 向 OS 申请一个当前空闲的回环 TCP 端口并返回其端口号。
    /// 实现：绑定到 0 号端口让 OS 自选，读取实际分配端口后立即释放。
    /// 释放与 codebuddy 绑定之间存在极短竞态窗口，但在回环地址上可忽略。
    /// </summary>
    public static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
