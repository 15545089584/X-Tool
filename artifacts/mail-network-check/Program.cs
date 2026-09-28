using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
await Task.WhenAll(new[]{"direct","system-http","socks5"}.Select(async mode => {
 using var client=new ImapClient { Timeout=12000 };
 client.ProxyClient= mode=="direct" ? null : mode=="socks5" ? new Socks5Client("127.0.0.1",7897) : new HttpProxyClient("127.0.0.1",7897);
 using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
 try { await client.ConnectAsync("imap.gmail.com",993,SecureSocketOptions.SslOnConnect,ct.Token); Console.WriteLine(mode+": TLS/IMAP success; no authentication attempted"); await client.DisconnectAsync(true); }
 catch(Exception ex) { for(var e=ex;e!=null;e=e.InnerException)Console.WriteLine(mode+": "+e.GetType().Name+": "+e.Message.Split(Environment.NewLine)[0]); }
}));
