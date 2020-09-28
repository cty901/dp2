﻿using System;
using System.Collections.Generic;
using System.Deployment.Application;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using DigitalPlatform;
using DigitalPlatform.WPF;
using DigitalPlatform.RFID;
using DigitalPlatform.Text;
using DigitalPlatform.LibraryClient.localhost;

namespace dp2SSL.Models
{
    // 全局监控任务
    public static class GlobalMonitor
    {
        static Task _monitorTask = null;

        // 是否需要重启计算机
        static bool _needReboot = false;
        // 最近一次检查升级的时刻
        static DateTime _lastUpdateTime;
        // 检查升级的时间间隔
        static TimeSpan _updatePeriod = TimeSpan.FromMinutes(60); // 2*60 两个小时

        // 成功升级的次数
        static int _updateSucceedCount = 0;

        // 监控间隔时间
        static TimeSpan _monitorIdleLength = TimeSpan.FromSeconds(10);

        static AutoResetEvent _eventMonitor = new AutoResetEvent(false);

        // 激活 Monitor 任务
        public static void ActivateMonitor()
        {
            _eventMonitor.Set();
        }

        // 启动一般监控任务
        public static void StartMonitorTask()
        {
            if (_monitorTask != null)
                return;

            CancellationToken token = App.CancelToken;

            token.Register(() =>
            {
                _eventMonitor.Set();
            });

            _monitorTask = Task.Factory.StartNew(async () =>
            {
                WpfClientInfo.WriteInfoLog("全局监控专用线程开始");
                try
                {
                    while (token.IsCancellationRequested == false)
                    {
                        // await Task.Delay(TimeSpan.FromSeconds(10));
                        _eventMonitor.WaitOne(_monitorIdleLength);

                        token.ThrowIfCancellationRequested();

                        // 清除 PasswordCache
                        PasswordCache.CleanIdlePassword(TimeSpan.FromSeconds(60));

                        // 检查小票打印机状态
                        var check_result = CheckPosPrinter();
                        if (check_result.Value == -1)
                            App.SetError("printer", "小票打印机状态异常");
                        else if (StringUtil.IsInList("paperout", check_result.ErrorCode))
                            App.SetError("printer", "小票打印机缺纸");
                        else if (StringUtil.IsInList("paperwillout", check_result.ErrorCode))
                            App.SetError("printer", "小票打印机即将缺纸");
                        else
                            App.SetError("printer", null);

                        // 检查升级绿色 dp2ssl
                        if (_needReboot == false
                        && StringUtil.IsDevelopMode() == false
                        && ApplicationDeployment.IsNetworkDeployed == false
                        && _updateSucceedCount == 0 // 一旦更新成功一次以后便不再更新
                        && DateTime.Now - _lastUpdateTime > _updatePeriod)
                        {
                            WpfClientInfo.WriteInfoLog("开始自动检查升级");
                            // result.Value:
                            //      -1  出错
                            //      0   经过检查发现没有必要升级
                            //      1   成功
                            //      2   成功，但需要立即重新启动计算机才能让复制的文件生效
                            var update_result = await GreenInstall.GreenInstaller.InstallFromWeb(GreenInstall.GreenInstaller.dp2ssl_weburl,  // "http://dp2003.com/dp2ssl/v1_dev",
                                "c:\\dp2ssl",
                                "delayExtract,updateGreenSetupExe,clearStateFile,debugInfo",
                                //true,
                                //true,
                                token,
                                null);
                            if (update_result.Value == -1)
                                WpfClientInfo.WriteErrorLog($"自动检查升级出错: {update_result.ErrorInfo}");
                            else
                                WpfClientInfo.WriteInfoLog($"结束自动检查升级 update_result:{update_result.ToString()}");

                            // 2020/9/1
                            WpfClientInfo.WriteInfoLog($"InstallFromWeb() 调试信息如下:\r\n{update_result.DebugInfo}");

                            if (update_result.Value == 1 || update_result.Value == 2)
                            {
                                _updateSucceedCount++;
                                if (update_result.Value == 1)
                                {
                                    App.TriggerUpdated("重启 dp2ssl(greensetup) 可使用新版本");
                                    PageShelf.TrySetMessage(null, "dp2SSL 升级文件已经下载成功，下次重启 dp2ssl(greensetup) 时可自动启用新版本");
                                }
                                else if (update_result.Value == 2)
                                {
                                    _needReboot = true;
                                    App.TriggerUpdated("重启计算机可使用新版本");
                                    PageShelf.TrySetMessage(null, "dp2SSL 升级文件已经下载成功，下次重启计算机时可自动升级到新版本");
                                }
                            }
                            _lastUpdateTime = DateTime.Now;
                        }

                        // 2020/9/15
                        // 检查升级 ClickOnce dp2ssl
                        if (StringUtil.IsDevelopMode() == false
                        && ApplicationDeployment.IsNetworkDeployed == true
                        && _updateSucceedCount == 0 // 一旦更新成功一次以后便不再更新
                        && DateTime.Now - _lastUpdateTime > _updatePeriod)
                        {
                            try
                            {
                                // result.Value:
                                //      -1  出错
                                //      0   没有发现新版本
                                //      1   发现新版本，重启后可以使用新版本
                                NormalResult result = WpfClientInfo.InstallUpdateSync();
                                WpfClientInfo.WriteInfoLog($"ClickOnce 后台升级 dp2ssl 返回: {result.ToString()}");
                                if (result.Value == -1)
                                    WpfClientInfo.WriteErrorLog($"升级出错: {result.ErrorInfo}");
                                else if (result.Value == 1)
                                {
                                    _updateSucceedCount++;
                                    WpfClientInfo.WriteInfoLog($"升级成功: {result.ErrorInfo}");
                                    App.TriggerUpdated(result.ErrorInfo);
                                    // MessageBox.Show(result.ErrorInfo);
                                }
                                else if (string.IsNullOrEmpty(result.ErrorInfo) == false)
                                {
                                    WpfClientInfo.WriteInfoLog($"{result.ErrorInfo}");
                                }
                            }
                            catch (Exception ex)
                            {
                                WpfClientInfo.WriteErrorLog($"后台 ClickOnce 自动升级出现异常: {ExceptionUtil.GetDebugText(ex)}");
                            }

                            _lastUpdateTime = DateTime.Now;
                        }
                    }
                    _monitorTask = null;
                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    WpfClientInfo.WriteErrorLog($"全局监控专用线程出现异常: {ExceptionUtil.GetDebugText(ex)}");
                    App.SetError("global_monitor", $"全局监控专用线程出现异常: {ex.Message}");
                }
                finally
                {
                    WpfClientInfo.WriteInfoLog("全局监控专用线程结束");
                }
            },
token,
TaskCreationOptions.LongRunning,
TaskScheduler.Default);
        }

        // 立即进行升级
        public static void ActivateUpdate()
        {
            if (_updateSucceedCount > 0)
            {
                PageShelf.TrySetMessage(null, "稍早已经更新过了，现在重启 dp2ssl 可以使用此新版本");
                return;
            }

            PageShelf.TrySetMessage(null, "开始更新");
            _lastUpdateTime = DateTime.MinValue;
            ActivateMonitor();
        }

        // 检查小票打印机状态
        static DigitalPlatform.NormalResult CheckPosPrinter()
        {
            if (string.IsNullOrEmpty(App.PosPrintStyle)
                || App.PosPrintStyle == "不打印")
                return new DigitalPlatform.NormalResult();

            if (string.IsNullOrEmpty(App.RfidUrl)
                || string.IsNullOrEmpty(RfidManager.Url))
                return new DigitalPlatform.NormalResult();

            var result = RfidManager.PosPrint("getstatus", "", "");
            if (result.Value == 0)
            {
                return result;
                /*
                if (StringUtil.IsInList("paperout", result.ErrorCode))
                    return new NormalResult { ValueTask = -1, ErrorCode = result.ErrorCode };
                */
            }
            return result;
        }
    }
}
