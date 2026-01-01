using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HidSharp; // 现代高性能 HID 库
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Hid2x360
{
    class Program
    {
        static bool is_init = true;
        static void Main(string[] args)
        {
            // 提升进程优先级，确保 Windows 优先调度本程序
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

            // 1. 初始化虚拟 Xbox 360 手柄
            ViGEmClient client;
            IXbox360Controller virtPad;
            try
            {
                client = new ViGEmClient();
                virtPad = client.CreateXbox360Controller();
                virtPad.Connect();
                //Console.WriteLine("虚拟 Xbox 360 手柄已载入系统！");
            }
            catch (Exception ex)
            {
                ConsoleManager.Show();
                Console.WriteLine($"ViGEm 初始化失败: {ex.Message}");
                Console.WriteLine("请前往https://github.com/nefarius/ViGEmBus/releases下载x360虚拟手柄");
                return;
            }

			while (true)
			{
			    ckrPad(virtPad);
			}

        }

        private static void ckrPad(IXbox360Controller vpad, int vid = 0x27F8, int pid = 0x0BBF, int timeout = 8)
		{
            // 2. 使用 HidSharp 查找设备
            var device = DeviceList.Local.GetHidDevices(vid, pid).FirstOrDefault();
            int retry = 0;
            while (true)
            {
                if (device != null)
                {
                    is_init = false;
                    retry = 0;
                    //Console.WriteLine($"检测到的手柄信息: {device?.ProductName} ({device?.DevicePath})");
                    break;
                }
                else if (!is_init && retry < 15)
                {
                    retry++;
                    System.Threading.Thread.Sleep(1000);
                    device = DeviceList.Local.GetHidDevices(vid, pid).FirstOrDefault();
                    if (device != null)
                    {break;}
                }
                else
                {
                    ConsoleManager.Show();
                    Console.WriteLine("未找到物理手柄，请检查连接或手柄的两个ID值。");
                    Console.WriteLine("按任意键继续重试15秒，按 'N' 退出...");
                    ConsoleKeyInfo keyInfo = Console.ReadKey(true);

                    if (keyInfo.Key == ConsoleKey.N)
                    {
                        Environment.Exit(0);
                    }
                    device = DeviceList.Local.GetHidDevices(vid, pid).FirstOrDefault();
                    Console.WriteLine(); // 换行
                }
            }

            // 3. 开启读取流
            if (device!.TryOpen(out HidStream stream))
            {
                using (stream)
                {
                    // 如果设置为 0ms，在没数据时会疯狂抛出异常，消耗 CPU。
                    stream.ReadTimeout = timeout; 
                    
                    byte[] inputBuffer = new byte[device.MaxInputReportLength];

                    //Console.WriteLine("正在变成x360的形状..");
                    ConsoleManager.Hide();

                    while (true)
                    {
                        try
                        {
                            stream.Read(inputBuffer);
                            feedvPad(inputBuffer, vpad);
                        }
                        catch (TimeoutException)
                        {
                            // 正常的超时，手柄此时没有发送新数据。我们直接跳过，进入下一次循环即可。
                            continue; 
                        }
                        catch (Exception ex)
                        {
                            //ConsoleManager.Show();
                            //Console.WriteLine($"读取中断: {ex.Message}");
                            break;
                        }
                    }
                }
            }
            //else
            //{
            //    ConsoleManager.Show();
            //    Console.WriteLine("无法打开手柄流，请检查是否有其他程序占用（如Steam）。");
            //}
		}
        private static void feedvPad(byte[] data, IXbox360Controller vpad)
        {
            // 注意：HidSharp 返回的 data[0] 通常是 Report ID 如果你的数据偏移不对，请尝试将索引全部 +1 或 -1
            
            if (data.Length < 12) return; //手柄输入数据长度过滤

            // --- 按钮映射 ---
            vpad.SetButtonState(Xbox360Button.A, (data[7] & 0x01) != 0);
            vpad.SetButtonState(Xbox360Button.B, (data[7] & 0x02) != 0);
            vpad.SetButtonState(Xbox360Button.X, (data[7] & 0x08) != 0);
            vpad.SetButtonState(Xbox360Button.Y, (data[7] & 0x10) != 0);
            vpad.SetButtonState(Xbox360Button.LeftShoulder, (data[7] & 0x40) != 0);
            vpad.SetButtonState(Xbox360Button.RightShoulder, (data[7] & 0x80) != 0);
            vpad.SetButtonState(Xbox360Button.LeftThumb, (data[8] & 0x20) != 0);
            vpad.SetButtonState(Xbox360Button.RightThumb, (data[8] & 0x40) != 0);
            vpad.SetButtonState(Xbox360Button.Back, (data[8] & 0x04) != 0);
            vpad.SetButtonState(Xbox360Button.Start, (data[8] & 0x08) != 0);
            vpad.SetButtonState(Xbox360Button.Guide, (data[8] & 0x10) != 0);
            mapDPad(data[5], vpad);
            //vpad.SetButtonState(Xbox360Button.Up, (data[5] & 0x00) != 0);
            //vpad.SetButtonState(Xbox360Button.Down, (data[5] & 0x40) != 0);
            //vpad.SetButtonState(Xbox360Button.Left, (data[5] & 0x60) != 0);
            //vpad.SetButtonState(Xbox360Button.Right, (data[5] & 0x20) != 0);

            // --- 摇杆映射 ---
            vpad.SetAxisValue(Xbox360Axis.LeftThumbX, convertSignedAxis(data[1]));
            vpad.SetAxisValue(Xbox360Axis.LeftThumbY, (short)(-convertSignedAxis(data[2])));
            vpad.SetAxisValue(Xbox360Axis.RightThumbX, convertSignedAxis(data[3]));
            vpad.SetAxisValue(Xbox360Axis.RightThumbY, (short)(-convertSignedAxis(data[4])));
            vpad.SetSliderValue(Xbox360Slider.LeftTrigger, data[11]);
            vpad.SetSliderValue(Xbox360Slider.RightTrigger, data[10]);
            // --- 提交报告 (非常重要，减少 ViGEm 内部延迟) ---
            // 在某些版本中该方法可能不可见，如果报错可注释掉
            vpad.SubmitReport(); 
        }

        private static short convertSignedAxis(byte value)
        {
            // 处理 0-255 到 -32768-32767 的转换
            // 255->128 为向上/左
            sbyte signedValue = (sbyte)value;
            return (short)(signedValue << 8);
        }
		private static short convertAxis(byte value)
        {
            // 公式：(Value - 128) * 256
            // 0 -> -32768
            // 128 -> 0
            // 255 -> ~32512
            return (short)((value - 128) * 256);
        }

        private static void mapDPad(byte val, IXbox360Controller vpad)
        {
            // 重置所有方向键状态
            vpad.SetButtonState(Xbox360Button.Up, false);
            vpad.SetButtonState(Xbox360Button.Right, false);
            vpad.SetButtonState(Xbox360Button.Down, false);
            vpad.SetButtonState(Xbox360Button.Left, false);

            // 如果值大于 120，通常认为是中心位（未按下）
            if (val > 127) return;

            // 使用区间判断，容错率更高
            if (val > 119 || val <= 8) // 上 (0 附近)
                vpad.SetButtonState(Xbox360Button.Up, true);
            
            if (val >= 24 && val < 40) // 右 (32 附近)
                vpad.SetButtonState(Xbox360Button.Right, true);
                
            if (val > 56 && val <= 72) // 下 (64 附近)
                vpad.SetButtonState(Xbox360Button.Down, true);
                
            if (val >= 88 && val < 104) // 左 (96 附近)
                vpad.SetButtonState(Xbox360Button.Left, true);

            // 处理斜向（复合方向）
            if (val > 8 && val < 24) { vpad.SetButtonState(Xbox360Button.Up, true); vpad.SetButtonState(Xbox360Button.Right, true); } //16
            if (val >= 40 && val <= 56) { vpad.SetButtonState(Xbox360Button.Down, true); vpad.SetButtonState(Xbox360Button.Right, true); } //48
            if (val > 72 && val < 88) { vpad.SetButtonState(Xbox360Button.Down, true); vpad.SetButtonState(Xbox360Button.Left, true); } //80
            if (val >= 104 && val <= 119) { vpad.SetButtonState(Xbox360Button.Up, true); vpad.SetButtonState(Xbox360Button.Left, true); } //112
        }

    }

    internal static class ConsoleManager
    {
        // 创建控制台窗口
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

        // 销毁控制台窗口
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleOutputCP(uint wCodePageID);

        public static void Show()
        {
            AllocConsole();
            // 重新导向标准输出流，否则 Console.WriteLine 依然无效
            SetConsoleOutputCP(65001);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
            var writer = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
            Console.Clear();
        }

        public static void Hide() => FreeConsole();
    }
}

