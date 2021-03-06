﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Threading;
using System.Deployment.Application;

using Microsoft.VisualStudio.Threading;
using static dp2SSL.LibraryChannelUtil;

using DigitalPlatform;
using DigitalPlatform.IO;
using DigitalPlatform.RFID;
using DigitalPlatform.SIP2;
using DigitalPlatform.Text;
using DigitalPlatform.WPF;
using DigitalPlatform.Xml;
using DigitalPlatform.LibraryClient.localhost;

namespace dp2SSL
{
    /// <summary>
    /// 和 SIP2 通道有关的功能
    /// </summary>
    public static class SipChannelUtil
    {
        static SipChannel _channel = new SipChannel(Encoding.UTF8);

        public static string DateFormat = "yyyy-MM-dd";

        static async Task<SipChannel> GetChannelAsync()
        {
            if (_channel.Connected == false)
            {
                /*
                var parts = StringUtil.ParseTwoPart(App.SipServerUrl, ":");
                string address = parts[0];
                string port = parts[1];

                if (Int32.TryParse(port, out int port_value) == false)
                    throw new Exception($"SIP 服务器和端口号字符串 '{App.SipServerUrl}' 中端口号部分 '{port}' 格式错误");

                var result = await _channel.ConnectionAsync(address,
                    port_value);
                */
                var result = await ConnectAsync();
                if (result.Value == -1) // 出错
                {
                    TryDetectSipNetwork();
                    throw new Exception($"连接 SIP 服务器 {App.SipServerUrl} 时出错: {result.ErrorInfo}");
                }

                var login_result = await _channel.LoginAsync(App.SipUserName,
                    App.SipPassword);
                if (login_result.Value == -1)
                    throw new Exception($"针对 SIP 服务器 {App.SipServerUrl} 登录出错: {login_result.ErrorInfo}");

                // TODO: ScStatus()

            }

            return _channel;
        }

        static void ReturnChannel(SipChannel channel)
        {

        }

        static async Task<NormalResult>  ConnectAsync()
        {
            var parts = StringUtil.ParseTwoPart(App.SipServerUrl, ":");
            string address = parts[0];
            string port = parts[1];

            if (Int32.TryParse(port, out int port_value) == false)
                throw new Exception($"SIP 服务器和端口号字符串 '{App.SipServerUrl}' 中端口号部分 '{port}' 格式错误");

            var result = await _channel.ConnectAsync(address,
    port_value);
            if (result.Value == -1) // 出错
            {
                // TryDetectSipNetwork();
                // throw new Exception($"连接 SIP 服务器 {App.SipServerUrl} 时出错: {result.ErrorInfo}");
            }

            return result;
        }

        static AsyncSemaphore _channelLimit = new AsyncSemaphore(1);


        // 获得册记录信息和书目摘要信息
        // parameters:
        //      style   风格。network 表示只从网络获取册记录；否则优先从本地获取，本地没有再从网络获取册记录。无论如何，书目摘要都是尽量从本地获取
        // .Value
        //      0   没有找到
        //      1   找到
        public static async Task<GetEntityDataResult> GetEntityDataAsync(string pii,
            string style)
        {
            bool network = StringUtil.IsInList("network", style);
            try
            {
                using (var releaser = await _channelLimit.EnterAsync())
                {

                    SipChannel channel = await GetChannelAsync();
                    try
                    {
                        GetEntityDataResult result = null;
                        List<NormalResult> errors = new List<NormalResult>();

                        EntityItem entity_record = null;

                        // ***
                        // 第一步：获取册记录

                        {
                            // 再尝试从 dp2library 服务器获取
                            // TODO: ItemXml 和 BiblioSummary 可以考虑在本地缓存一段时间
                            int nRedoCount = 0;
                        REDO_GETITEMINFO:
                            var get_result = await _channel.GetItemInfoAsync("", pii);
                            if (get_result.Value == -1)
                                errors.Add(new NormalResult
                                {
                                    Value = -1,
                                    ErrorInfo = get_result.ErrorInfo,
                                    ErrorCode = get_result.ErrorCode
                                });
                            else if (get_result.Result.CirculationStatus_2 == "01")
                            {
                                errors.Add(new NormalResult
                                {
                                    Value = -1,
                                    ErrorInfo = get_result.Result.AF_ScreenMessage_o,
                                    ErrorCode = get_result.Result.CirculationStatus_2
                                });
                            }
                            else if (get_result.Result.CirculationStatus_2 == "13")
                            {
                                errors.Add(new NormalResult
                                {
                                    Value = -1,
                                    ErrorInfo = get_result.Result.AF_ScreenMessage_o,
                                    ErrorCode = "itemNotFound"
                                });
                            }
                            else
                            {


                                XmlDocument itemdom = new XmlDocument();
                                itemdom.LoadXml("<root />");

                                string state = "";
                                if (get_result.Result.CirculationStatus_2 == "12")
                                    state = "丢失";

                                DomUtil.SetElementText(itemdom.DocumentElement, "state", state);

                                DomUtil.SetElementText(itemdom.DocumentElement,
                                    "barcode",
                                    get_result.Result.AB_ItemIdentifier_r);
                                DomUtil.SetElementText(itemdom.DocumentElement,
    "location",
    get_result.Result.AQ_PermanentLocation_o);
                                DomUtil.SetElementText(itemdom.DocumentElement,
"currentLocation",
get_result.Result.AP_CurrentLocation_o);

                                DomUtil.SetElementText(itemdom.DocumentElement,
"accessNo",
get_result.Result.CH_ItemProperties_o);

                                // 借书时间
                                {
                                    string borrowDateString = get_result.Result.CM_HoldPickupDate_18;
                                    if (string.IsNullOrEmpty(borrowDateString) == false)
                                    {
                                        if (DateTime.TryParseExact(borrowDateString,
                                        "yyyyMMdd    HHmmss",
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.None,
                                        out DateTime borrowDate))
                                        {
                                            DomUtil.SetElementText(itemdom.DocumentElement,
            "borrowDate",
            DateTimeUtil.Rfc1123DateTimeStringEx(borrowDate));

                                            DomUtil.SetElementText(itemdom.DocumentElement,
"borrower",
"***");
                                        }
                                        else
                                        {
                                            // 报错，时间字符串格式错误，无法解析
                                        }
                                    }
                                }

                                // 应还书时间
                                {
                                    string returnningDateString = get_result.Result.AH_DueDate_o;
                                    if (string.IsNullOrEmpty(returnningDateString) == false)
                                    {
                                        if (DateTime.TryParseExact(returnningDateString,
                                        DateFormat,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.None,
                                        out DateTime returningDate))
                                        {
                                            DomUtil.SetElementText(itemdom.DocumentElement,
            "returningDate",
            DateTimeUtil.Rfc1123DateTimeStringEx(returningDate));
                                        }
                                        else
                                        {
                                            // 报错，时间字符串格式错误，无法解析
                                        }
                                    }
                                }

                                result = new GetEntityDataResult
                                {
                                    Value = 1,
                                    ItemXml = itemdom.OuterXml,
                                    ItemRecPath = get_result.Result.AB_ItemIdentifier_r,
                                    Title = get_result.Result.AJ_TitleIdentifier_r,
                                };

                                /*
                                // 保存到本地数据库
                                await AddOrUpdateAsync(context, new EntityItem
                                {
                                    PII = pii,
                                    Xml = item_xml,
                                    RecPath = item_recpath,
                                    Timestamp = timestamp,
                                });
                                */
                            }
                        }

                        // ***
                        /// 第二步：获取书目摘要

                        // 完全成功
                        if (result != null && errors.Count == 0)
                            return result;
                        if (result == null)
                            return new GetEntityDataResult
                            {
                                Value = errors[0].Value,
                                ErrorInfo = errors[0].ErrorInfo,
                                ErrorCode = errors[0].ErrorCode
                            };
                        result.ErrorInfo = errors[0].ErrorInfo;
                        result.ErrorCode = errors[0].ErrorCode;
                        return result;
                    }
                    finally
                    {
                        ReturnChannel(channel);
                    }
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"GetEntityDataAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                return new GetEntityDataResult
                {
                    Value = -1,
                    ErrorInfo = $"GetEntityDataAsync() 出现异常: {ex.Message}",
                    ErrorCode = ex.GetType().ToString()
                };
            }
        }

        // return.Value:
        //      -1  出错
        //      0   读者记录没有找到
        //      1   成功
        public static async Task<GetReaderInfoResult> GetReaderInfoAsync(string pii)
        {
            try
            {
                using (var releaser = await _channelLimit.EnterAsync())
                {
                    SipChannel channel = await GetChannelAsync();
                    try
                    {
                        List<NormalResult> errors = new List<NormalResult>();

                        int nRedoCount = 0;
                    REDO_GETITEMINFO:
                        var get_result = await _channel.GetPatronInfoAsync(pii);
                        if (get_result.Value == -1)
                            return new GetReaderInfoResult
                            {
                                Value = -1,
                                ErrorInfo = get_result.ErrorInfo,
                                ErrorCode = get_result.ErrorCode
                            };
                        /*
                        else if (get_result.Result.CirculationStatus_2 == "01")
                        {
                            errors.Add(new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = get_result.Result.AF_ScreenMessage_o,
                                ErrorCode = get_result.Result.CirculationStatus_2
                            });
                        }
                        else if (get_result.Result.CirculationStatus_2 == "13")
                        {
                            errors.Add(new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = get_result.Result.AF_ScreenMessage_o,
                                ErrorCode = "itemNotFound"
                            });
                        }
                        */
                        else
                        {
                            XmlDocument readerdom = new XmlDocument();
                            readerdom.LoadXml("<root />");

                            // 证状态
                            string state = "***";
                            if (get_result.Result.BL_ValidPatron_o == "Y")
                                state = "";
                            DomUtil.SetElementText(readerdom.DocumentElement,
                                "state",
                                state);

                            // 读者证条码号
                            DomUtil.SetElementText(readerdom.DocumentElement,
                                "barcode",
                                get_result.Result.AA_PatronIdentifier_r);

                            // 姓名
                            DomUtil.SetElementText(readerdom.DocumentElement,
"name",
get_result.Result.AE_PersonalName_r);

                            // 可借册数
                            Patron.SetParamValue(readerdom.DocumentElement, "当前还可借", get_result.Result.BZ_HoldItemsLimit_o);
                            Patron.SetParamValue(readerdom.DocumentElement, "可借总册数", get_result.Result.CB_ChargedItemsLimit_o);

                            // 在借册
                            var root = readerdom.DocumentElement.AppendChild(readerdom.CreateElement("borrows")) as XmlElement;
                            var items = get_result.Result.AU_ChargedItems_o;
                            if (items != null)
                            {
                                foreach (var item in items)
                                {
                                    if (item.Value == null)
                                        continue;
                                    var borrow = root.AppendChild(readerdom.CreateElement("borrow")) as XmlElement;
                                    borrow.SetAttribute("barcode", item.Value);
                                }
                            }

                            return new GetReaderInfoResult
                            {
                                Value = 1,
                                ReaderXml = readerdom.OuterXml,
                                RecPath = "",
                                Timestamp = null
                            };
                        }
                    }
                    finally
                    {
                        ReturnChannel(channel);
                    }
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"GetEntGetReaderInfoAsyncityDataAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                return new GetReaderInfoResult
                {
                    Value = -1,
                    ErrorInfo = $"GetReaderInfoAsync() 出现异常: {ex.Message}",
                    ErrorCode = ex.GetType().ToString()
                };
            }
        }

        public static async Task<NormalResult> BorrowAsync(string patronBarcode,
            string itemBarcode)
        {
            try
            {
                using (var releaser = await _channelLimit.EnterAsync())
                {

                    SipChannel channel = await GetChannelAsync();
                    try
                    {
                        var result = await channel.CheckoutAsync(patronBarcode, itemBarcode);
                        if (result.Value == -1)
                            return new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        if (result.Value == 0)
                            return new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        return new NormalResult();
                    }
                    finally
                    {
                        ReturnChannel(channel);
                    }
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"BorrowAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                return new GetEntityDataResult
                {
                    Value = -1,
                    ErrorInfo = $"BorrowAsync() 出现异常: {ex.Message}",
                    ErrorCode = ex.GetType().ToString()
                };
            }
        }

        public static async Task<NormalResult> ReturnAsync(string itemBarcode)
        {
            try
            {
                using (var releaser = await _channelLimit.EnterAsync())
                {

                    SipChannel channel = await GetChannelAsync();
                    try
                    {
                        var result = await channel.CheckinAsync(itemBarcode);
                        if (result.Value == -1)
                            return new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        if (result.Value == 0)
                            return new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        return new NormalResult();
                    }
                    finally
                    {
                        ReturnChannel(channel);
                    }
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"ReturnAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                return new GetEntityDataResult
                {
                    Value = -1,
                    ErrorInfo = $"ReturnAsync() 出现异常: {ex.Message}",
                    ErrorCode = ex.GetType().ToString()
                };
            }
        }

        public static async Task<NormalResult> RenewAsync(string patronBarcode,
    string itemBarcode)
        {
            try
            {
                using (var releaser = await _channelLimit.EnterAsync())
                {

                    SipChannel channel = await GetChannelAsync();
                    try
                    {
                        var result = await channel.RenewAsync(patronBarcode, itemBarcode);
                        if (result.Value == -1)
                            return new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        if (result.Value == 0)
                            return new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        return new NormalResult();
                    }
                    finally
                    {
                        ReturnChannel(channel);
                    }
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"RenewAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                return new GetEntityDataResult
                {
                    Value = -1,
                    ErrorInfo = $"RenewAsync() 出现异常: {ex.Message}",
                    ErrorCode = ex.GetType().ToString()
                };
            }
        }

        static async Task<NormalResult> DetectSipNetworkAsync()
        {
            /*
            // testing
            return new NormalResult { Value = 1 };
            */
            try
            {
                using (var releaser = await _channelLimit.EnterAsync())
                {
                    SipChannel channel = await GetChannelAsync();
                    try
                    {
                        // -1出错，0不在线，1正常
                        var result = await channel.ScStatusAsync();
                        if (result.Value == -1)
                            return new NormalResult
                            {
                                Value = -1,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        if (result.Value == 0)
                            return new NormalResult
                            {
                                Value = 0,
                                ErrorInfo = result.ErrorInfo,
                                ErrorCode = result.ErrorCode
                            };
                        return new NormalResult
                        {
                            Value = result.Value,
                            ErrorInfo = result.ErrorInfo,
                            ErrorCode = result.ErrorCode
                        };
                    }
                    finally
                    {
                        ReturnChannel(channel);
                    }
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"DetectSipNetworkAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                return new GetEntityDataResult
                {
                    Value = -1,
                    ErrorInfo = $"DetectSipNetworkAsync() 出现异常: {ex.Message}",
                    ErrorCode = ex.GetType().ToString()
                };
            }
        }


        #region 监控

        // 可以适当降低探测的频率。比如每五分钟探测一次
        // 两次检测网络之间的间隔
        static TimeSpan _detectPeriod = TimeSpan.FromMinutes(5);
        // 最近一次检测网络的时间
        static DateTime _lastDetectTime;

        static Task _monitorTask = null;

        // 是否已经(升级)更新了
        static bool _updated = false;
        // 最近一次检查升级的时刻
        static DateTime _lastUpdateTime;
        // 检查升级的时间间隔
        static TimeSpan _updatePeriod = TimeSpan.FromMinutes(2 * 60); // 2*60 两个小时

        // 监控间隔时间
        static TimeSpan _monitorIdleLength = TimeSpan.FromSeconds(10);

        static AutoResetEvent _eventMonitor = new AutoResetEvent(false);

        // 激活 Monitor 任务
        public static void ActivateMonitor()
        {
            _eventMonitor.Set();
        }

        static Task _delayTry = null;

        // 立即安排一次检测 SIP 网络
        public static void TryDetectSipNetwork(bool delay = true)
        {
            /*
            // testing
            return;
            */

            if (_delayTry != null)
                return;

            _delayTry = Task.Run(async () =>
            {
                try
                {
                    if (delay)
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    _lastDetectTime = DateTime.MinValue;
                    ActivateMonitor();
                    _delayTry = null;
                }
                catch
                {

                }
            });
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
                WpfClientInfo.WriteInfoLog("SIP 监控专用线程开始");
                try
                {
                    while (token.IsCancellationRequested == false)
                    {
                        // await Task.Delay(TimeSpan.FromSeconds(10));
                        _eventMonitor.WaitOne(_monitorIdleLength);

                        token.ThrowIfCancellationRequested();

                        if (DateTime.Now - _lastDetectTime > _detectPeriod)
                        {
                            var detect_result = await DetectSipNetworkAsync();
                            _lastDetectTime = DateTime.Now;

                            // testing
                            //detect_result.Value = -1;
                            //detect_result.ErrorInfo = "测试文字";

                            if (detect_result.Value != 1)
                                App.OpenErrorWindow(detect_result.ErrorInfo);
                            else
                                App.CloseErrorWindow();
                        }
                    }
                    _monitorTask = null;

                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    WpfClientInfo.WriteErrorLog($"SIP 监控专用线程出现异常: {ExceptionUtil.GetDebugText(ex)}");
                    App.SetError("monitor", $"SIP 监控专用线程出现异常: {ex.Message}");
                }
                finally
                {
                    WpfClientInfo.WriteInfoLog("SIP 监控专用线程结束");
                }
            },
token,
TaskCreationOptions.LongRunning,
TaskScheduler.Default);
        }

        #endregion
    }
}
