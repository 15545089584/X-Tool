using System.Text;
using ScreenshotApp.DesktopPet;

var target = new PetInjectionService.Target(new IntPtr(1), new IntPtr(2));
var output = new StringBuilder();
var percentages = new List<int>();
var progress = new InlineProgress(percentages.Add);
int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); passed++; }
bool Send(char ch) { output.Append(ch); return true; }
await PetInjectionService.WriteCoreAsync("中文 A+^%(){}~\r\n下一行\t😀", progress, CancellationToken.None, () => target, () => false, () => false, Send);
Check(output.ToString() == "中文 A+^%(){}~\n下一行    😀", "中文、特殊符号、换行、缩进和代理对顺序保留");
Check(percentages.Last() == 100 && percentages.SequenceEqual(percentages.Order()), "进度单调且最终完成");
output.Clear(); int captures = 0;
try { await PetInjectionService.WriteCoreAsync("abc", progress, CancellationToken.None, () => ++captures < 3 ? target : target with { Focus = new IntPtr(3) }, () => false, () => false, Send); throw new Exception("焦点变化未停止"); }
catch (InvalidOperationException) { Check(output.ToString() == "a", "同窗口切换输入框立即停止，不写入新焦点"); }
output.Clear();
try { await PetInjectionService.WriteCoreAsync("abc", progress, CancellationToken.None, () => target, () => true, () => false, Send); throw new Exception("Esc 未停止"); }
catch (OperationCanceledException) { Check(output.Length == 0, "Esc 取消不继续写入"); }
try { await PetInjectionService.WriteCoreAsync("abc", progress, CancellationToken.None, () => target, () => false, () => true, Send); throw new Exception("修饰键未停止"); }
catch (InvalidOperationException) { Check(output.Length == 0, "修饰键保护避免快捷键副作用"); }
try { await PetInjectionService.WriteCoreAsync("abc", progress, CancellationToken.None, () => target, () => false, () => false, _ => false); throw new Exception("发送失败未停止"); }
catch (InvalidOperationException) { Check(true, "系统拒绝输入时报告失败"); }
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    try { await PetInjectionService.WriteCoreAsync("abc", progress, cancel.Token, () => throw new Exception("不应读取焦点"), () => false, () => false, Send); }
    catch (OperationCanceledException) { Check(true, "取消后不读取目标或发送按键"); }
}
await PetInjectionService.WriteCoreAsync("a\0b\b", progress, CancellationToken.None, () => target, () => false, () => false, Send);
Check(output.ToString() == "ab", "粘贴内容的控制字符不能触发删除或命令");
Console.WriteLine($"通过 {passed} 项；未向真实用户窗口发送按键。");
sealed class InlineProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
namespace ScreenshotApp.Capture { internal static class NativeMethods { internal static bool SendUnicodeCharacter(char ch) => throw new Exception("测试不得注入真实按键"); } }
