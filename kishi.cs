using System;
using System.Linq;
using System.Diagnostics;
using HidSharp; // 现代高性能 HID 库
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace KishiDriver
{
    class Program
    {
        // TODO: 替换为你手柄的 VID 和 PID
        private const int MyVendorId = 0x27F8; 
        private const int MyProductId = 0x0BBF;

        static void Main(string[] args)
        {
            // 提升进程优先级，确保 Windows 优先调度本程序
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

            // 1. 初始化虚拟 Xbox 360 手柄
            ViGEmClient client;
            IXbox360Controller virtualXbox;
            try
            {
                client = new ViGEmClient();
                virtualXbox = client.CreateXbox360Controller();
                virtualXbox.Connect();
                Console.WriteLine("虚拟 Xbox 360 手柄已插入系统。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ViGEm 初始化失败: {ex.Message}");
                return;
            }

            // 2. 使用 HidSharp 查找设备
            var device = DeviceList.Local.GetHidDevices(MyVendorId, MyProductId).FirstOrDefault();

            if (device == null)
            {
                Console.WriteLine("未找到物理手柄，请检查连接。");
                return;
            }

            Console.WriteLine($"找到设备: {device.ProductName} ({device.DevicePath})");

            // 3. 开启读取流
            if (device.TryOpen(out HidStream stream))
            {
                using (stream)
                {
                    // 建议设置为 10ms。
                    // 如果设置为 0，在没数据时会疯狂抛出异常，消耗 CPU。
                    stream.ReadTimeout = 10; 
                    
                    byte[] inputBuffer = new byte[device.MaxInputReportLength];

                    Console.WriteLine("正在变成x360的形状..");

                    while (true)
                    {
                        try
                        {
                            // Read 会阻塞直到数据到达或 10ms 超时
                            int bytesRead = stream.Read(inputBuffer, 0, inputBuffer.Length);

                            if (bytesRead > 0)
                            {
                                ProcessAndMap(inputBuffer, virtualXbox);
                            }
                        }
                        catch (TimeoutException)
                        {
                            // 正常的超时，手柄此时没有发送新数据。
                            // 我们直接跳过，进入下一次循环即可。
                            continue; 
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"读取中断: {ex.Message}");
                            break;
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("无法打开手柄流，请检查是否有其他程序占用（如 Steam）。");
            }
        }

        private static void ProcessAndMap(byte[] data, IXbox360Controller xbox)
        {
            // 注意：HidSharp 返回的 data[0] 通常是 Report ID
            // 如果你的数据偏移不对，请尝试将索引全部 +1 或 -1
            
            if (data.Length < 11) return;

            // --- 按钮映射 ---
            xbox.SetButtonState(Xbox360Button.A, (data[7] & 0x01) != 0);
            xbox.SetButtonState(Xbox360Button.B, (data[7] & 0x02) != 0);
            xbox.SetButtonState(Xbox360Button.X, (data[7] & 0x08) != 0);
            xbox.SetButtonState(Xbox360Button.Y, (data[7] & 0x10) != 0);
            xbox.SetButtonState(Xbox360Button.LeftShoulder, (data[7] & 0x40) != 0);
            xbox.SetButtonState(Xbox360Button.RightShoulder, (data[7] & 0x80) != 0);
            xbox.SetButtonState(Xbox360Button.LeftThumb, (data[8] & 0x20) != 0);
            xbox.SetButtonState(Xbox360Button.RightThumb, (data[8] & 0x40) != 0);
            xbox.SetButtonState(Xbox360Button.Back, (data[8] & 0x04) != 0);
            xbox.SetButtonState(Xbox360Button.Start, (data[8] & 0x08) != 0);
            xbox.SetButtonState(Xbox360Button.Guide, (data[8] & 0x10) != 0);
            MapKishiDPad(data[5], xbox);
            //xbox.SetButtonState(Xbox360Button.Up, (data[5] & 0x00) != 0);
            //xbox.SetButtonState(Xbox360Button.Down, (data[5] & 0x40) != 0);
            //xbox.SetButtonState(Xbox360Button.Left, (data[5] & 0x60) != 0);
            //xbox.SetButtonState(Xbox360Button.Right, (data[5] & 0x20) != 0);

            // --- 摇杆映射 ---
            xbox.SetAxisValue(Xbox360Axis.LeftThumbX, ConvertSignedAxis(data[1]));
            xbox.SetAxisValue(Xbox360Axis.LeftThumbY, (short)(-ConvertSignedAxis(data[2])));
            xbox.SetAxisValue(Xbox360Axis.RightThumbX, ConvertSignedAxis(data[3]));
            xbox.SetAxisValue(Xbox360Axis.RightThumbY, (short)(-ConvertSignedAxis(data[4])));
            xbox.SetSliderValue(Xbox360Slider.LeftTrigger, data[11]);
            xbox.SetSliderValue(Xbox360Slider.RightTrigger, data[10]);
            // --- 提交报告 (非常重要，减少 ViGEm 内部延迟) ---
            // 在某些版本中该方法可能不可见，如果报错可注释掉
            xbox.SubmitReport(); 
        }

        private static short ConvertSignedAxis(byte value)
        {
            // 处理 0-255 到 -32768-32767 的转换
            // 255->128 为向上/左
            sbyte signedValue = (sbyte)value;
            return (short)(signedValue << 8);
        }

        private static void MapKishiDPad(byte val, IXbox360Controller xbox)
        {
            // 重置所有方向键状态
            xbox.SetButtonState(Xbox360Button.Up, false);
            xbox.SetButtonState(Xbox360Button.Down, false);
            xbox.SetButtonState(Xbox360Button.Left, false);
            xbox.SetButtonState(Xbox360Button.Right, false);

            // 如果值大于 120，通常认为是中心位（未按下）
            if (val > 120) return;

            // 使用区间判断，容错率更高
            if (val >= 340 || val <= 10) // 上 (0 附近)
                xbox.SetButtonState(Xbox360Button.Up, true);
            
            if (val >= 22 && val <= 42) // 右 (32 附近)
                xbox.SetButtonState(Xbox360Button.Right, true);
                
            if (val >= 54 && val <= 74) // 下 (64 附近)
                xbox.SetButtonState(Xbox360Button.Down, true);
                
            if (val >= 86 && val <= 106) // 左 (96 附近)
                xbox.SetButtonState(Xbox360Button.Left, true);

            // 处理斜向（复合方向）
            if (val > 10 && val < 22) { xbox.SetButtonState(Xbox360Button.Up, true); xbox.SetButtonState(Xbox360Button.Right, true); }
            if (val > 42 && val < 54) { xbox.SetButtonState(Xbox360Button.Down, true); xbox.SetButtonState(Xbox360Button.Right, true); }
            if (val > 74 && val < 86) { xbox.SetButtonState(Xbox360Button.Down, true); xbox.SetButtonState(Xbox360Button.Left, true); }
            if (val > 106 && val < 120) { xbox.SetButtonState(Xbox360Button.Up, true); xbox.SetButtonState(Xbox360Button.Left, true); }
        }
    }
}

