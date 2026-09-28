using System.Net;
using ScreenshotApp.Collaboration;

var cases = new (string Local, string Peer, int Prefix, bool Expected)[]
{
    ("10.176.123.130", "10.176.123.168", 24, true),
    ("192.168.31.213", "10.176.123.168", 24, false),
    ("172.20.10.2", "172.20.10.1", 28, true),
    ("172.20.10.2", "172.20.10.16", 28, false),
    ("10.176.123.130", "10.176.127.1", 20, true),
    ("10.176.123.130", "10.176.128.1", 20, false),
    ("127.0.0.1", "::1", 24, false),
    ("10.1.1.1", "10.1.1.2", 0, false),
    ("10.1.1.1", "10.1.1.2", 33, false),
    ("10.1.1.1", "10.1.1.1", 32, true),
    ("10.1.1.1", "10.1.1.2", 32, false)
};
foreach (var item in cases)
{
    var actual = CollaborationLanAddress.IsSameSubnet(IPAddress.Parse(item.Local), IPAddress.Parse(item.Peer), item.Prefix);
    if (actual != item.Expected) throw new InvalidOperationException($"网段选择回归失败：{item.Local}/{item.Prefix} -> {item.Peer}");
}
Console.WriteLine($"PASS {cases.Length} 项热点、多网卡和掩码边界回归（未启动协作服务）");
