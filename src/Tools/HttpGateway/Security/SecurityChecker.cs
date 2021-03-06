using System;
using System.Text.RegularExpressions;
using Agebull.Common.Ioc;
using Agebull.Common.Logging;
using Agebull.Common.OAuth;
using Agebull.Common.Rpc;
using Agebull.ZeroNet.ZeroApi;
using Gboxt.Common.DataModel;
using Newtonsoft.Json;
using ZeroNet.Http.Route;

namespace ZeroNet.Http.Gateway
{
    /// <summary>
    ///     安全检查员
    /// </summary>
    public class SecurityChecker
    {
        //private static readonly Dictionary<string, bool> Keys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        ///     Http调用
        /// </summary>
        public RouteData Data { get; set; }

        #region 返回值检查

        /// <summary>
        ///     检查返回值是否合理
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        internal static bool CheckResult(RouteData data)
        {
            if (!RouteOption.Option.SystemConfig.CheckResult)
                return true;
            if (data.Status != ZeroOperatorStatus.Success || data.HostName == null)
                return false;
            if (string.IsNullOrWhiteSpace(data.ResultMessage))
            {
                IocHelper.Create<IRuntimeWaring>().Waring(data.HostName, data.ApiName, "返回值非法(空内容)");
                data.Status = ZeroOperatorStatus.FormalError;
                return false;
            }

            var json = data.ResultMessage.Trim();
            switch (json[0])
            {
                case '{':
                case '[':
                    break;
                default:
                    IocHelper.Create<IRuntimeWaring>()?.Waring(data.HostName, data.ApiName, "返回值非法(空内容)");
                    return true;
            }

            ApiResult result;
            try
            {
                result = JsonConvert.DeserializeObject<ApiResult>(data.ResultMessage);
                if (result == null)
                {
                    IocHelper.Create<IRuntimeWaring>()?.Waring(data.HostName, data.ApiName, "返回值非法(空内容)");
                    data.Status = ZeroOperatorStatus.FormalError;
                    return false;
                }
            }
            catch
            {
                IocHelper.Create<IRuntimeWaring>()?.Waring(data.HostName, data.ApiName, "返回值非法(空内容)");
                data.Status = ZeroOperatorStatus.FormalError;
                return false;
            }

            if (result.Status == null || result.Success)
                return true;
            switch (result.Status.ErrorCode)
            {
                case ErrorCode.ReTry:
                case ErrorCode.Ignore:
                    data.Status = ZeroOperatorStatus.NotReady;
                    return false;
                case ErrorCode.LogicalError:
                    data.Status = ZeroOperatorStatus.LogicalError;
                    return false;
                case ErrorCode.DenyAccess:
                case ErrorCode.Auth_RefreshToken_Unknow:
                case ErrorCode.Auth_ServiceKey_Unknow:
                case ErrorCode.Auth_AccessToken_Unknow:
                case ErrorCode.Auth_User_Unknow:
                case ErrorCode.Auth_Device_Unknow:
                case ErrorCode.Auth_AccessToken_TimeOut:
                    data.Status = ZeroOperatorStatus.DenyAccess;
                    return false;
            }
            data.Status = ZeroOperatorStatus.FormalError;
            IocHelper.Create<IRuntimeWaring>()?.Waring(data.HostName, data.ApiName, result.Status?.ClientMessage ?? "处理错误但无消息");
            return false;
        }


        #endregion
        #region 令牌检查

        /// <summary>
        ///     针对HttpHeader特征阻止不安全访问
        /// </summary>
        /// <returns></returns>
        private bool CheckApisInner()
        {
            //if (Data.ApiItem == null)
                return true;
            if (!string.IsNullOrWhiteSpace(Data.ApiItem.Os))
                if (Data.Token.IndexOf(Data.ApiItem.Os, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            if (string.IsNullOrWhiteSpace(Data.ApiItem.App))
                return true;
            return Data.Token.IndexOf(Data.ApiItem.App, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static readonly string UnknowDeviceJson = JsonConvert.SerializeObject(ApiResult.Error(ErrorCode.Auth_Device_Unknow));

        private static readonly string UnknowAccessTokenJson = JsonConvert.SerializeObject(ApiResult.Error(ErrorCode.Auth_AccessToken_Unknow));

        /// <summary>
        ///     执行检查
        /// </summary>
        /// <returns>
        ///     0:表示通过验证，可以继续
        ///     1：令牌为空或不合格
        ///     2：令牌是伪造的
        /// </returns>
        public bool CheckToken()
        {
            try
            {
                if (!CheckApisInner())
                {
                    Data.ResultMessage = ApiResult.DenyAccessJson;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(Data.Token))
                {
                    GlobalContext.SetUser(LoginUserInfo.CreateAnymouse(Data.Token, "*", "*"));
                    return true;
                    if (Data.ApiItem != null && Data.ApiItem.NoBearer)
                    {
                        GlobalContext.SetUser(LoginUserInfo.CreateAnymouse(Data.Token, "*", "*"));
                        return true;
                    }
                    Data.ResultMessage = ApiResult.DenyAccessJson;
                    return false;
                }
                for (var index = 1; index < Data.Token.Length; index++)
                {
                    var ch = Data.Token[index];
                    if (ch >= '0' && ch <= '9' || ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch == '_')
                        continue;
                    LogRecorder.MonitorTrace("Token Layout Error");
                    Data.ResultMessage = ApiResult.DenyAccessJson;
                    return false;
                }
                if (RouteOption.Option.Security.DenyTokens.ContainsKey(Data.Token))
                {
                    Data.ResultMessage = ApiResult.DenyAccessJson;
                    return false;
                }
                if (!RouteOption.Option.Security.CheckBearer)
                {
                    GlobalContext.SetUser(LoginUserInfo.CreateAnymouse(Data.Token, "*", "*"));
                    return true;
                }

                //var header = Request.Headers.Values.LinkToString(" ");
                //if (string.IsNullOrWhiteSpace(header) || header.Contains("iToolsVM"))
                //    return false;
                ApiResult<LoginUserInfo> result;
                switch (Data.Token[0])
                {
                    default:
                        Data.ResultMessage = ApiResult.DenyAccessJson;
                        return false;
                    case '*':
                        if (Data.ApiItem.NeedLogin)
                        {
                            Data.ResultMessage = ApiResult.DenyAccessJson;
                            return false;
                        }
                        result = CheckToken("DeviceId", RouteOption.Option.Security.DeviceIdCheckApi, out var vl);
                        if (result == null || result.Status.ErrorCode == ErrorCode.Auth_UnknowToken)
                        {
                            Data.ResultMessage = UnknowDeviceJson;
                            return false;
                        }
                        if (result.Success)
                        {
                            GlobalContext.SetUser(result.ResultData);
                            GlobalContext.SetOrganizational(new OrganizationalInfo
                            {
                                OrgId = result.ResultData.OrganizationId,
                                Name = result.ResultData.Organization,
                                OrgKey = result.ResultData.Organization,
                                RouteName = result.ResultData.Organization
                            });
                            return true;
                        }
                        Data.ResultMessage = vl;
                        return false;
                    case '#':
                        result = CheckToken("AccessToken", RouteOption.Option.Security.AccessTokenCheckApi, out vl);
                        if (result == null || result.Status.ErrorCode == ErrorCode.Auth_UnknowToken)
                        {
                            Data.ResultMessage = UnknowAccessTokenJson;
                            return false;
                        }
                        if (result.Success)
                        {
                            GlobalContext.SetUser(result.ResultData);
                            return true;
                        }
                        Data.ResultMessage = vl;
                        return false;
                }
            }
            catch (Exception e)
            {
                LogRecorder.Exception(e);
                return true;
            }
        }
        static readonly ApiResult<LoginUserInfo> DenyAccessResult = ApiResult.Error<LoginUserInfo>(ErrorCode.DenyAccess, null, null, "gateway", null, null);
        private ApiResult<LoginUserInfo> CheckToken(string name, string api, out string result)
        {
            // 远程调用
            using (MonitorScope.CreateScope($"Check{name}:{Data.Token}"))
            {
                ApiClient caller = new ApiClient
                {
                    Simple = true,
                    Station = RouteOption.Option.Security.AuthStation,
                    Commmand = api,
                    Argument = $"{{\"Token\":\"{Data.Token}\"}}"
                };
                caller.CallCommand();

                result = caller.Result;
                LogRecorder.MonitorTrace($"Result:{caller.Result}");
                return caller.Result == null
                    ? ApiResult.Succees(LoginUserInfo.CreateAnymouse(Data.Token, "*", "*"))
                    : JsonConvert.DeserializeObject<ApiResult<LoginUserInfo>>(caller.Result) ?? DenyAccessResult;
            }
        }
        #endregion

        #region 预检

        /// <summary>
        ///     验签
        /// </summary>
        /// <returns></returns>
        internal bool CheckSign()
        {
            return true;
        }

        /// <summary>
        ///     预检
        /// </summary>
        /// <returns></returns>
        public bool PreCheck()
        {
            return CheckSign() && KillDenyHttpHeaders();
        }

        /// <summary>
        ///     针对HttpHeader特征阻止不安全访问
        /// </summary>
        /// <returns></returns>
        internal bool KillDenyHttpHeaders()
        {
            try
            {
                foreach (var head in RouteOption.Option.Security.DenyHttpHeaders)
                {
                    if (!Data.Headers.ContainsKey(head.Head))
                        continue;
                    switch (head.DenyType)
                    {
                        case DenyType.Hase:
                            if (Data.Headers.ContainsKey(head.Head))
                                return false;
                            break;
                        case DenyType.NonHase:
                            if (!Data.Headers.ContainsKey(head.Head))
                                return false;
                            break;
                        case DenyType.Count:
                            if (!Data.Headers.ContainsKey(head.Head))
                                break;
                            if (Data.Headers[head.Head].Count == int.Parse(head.Value))
                                return false;
                            break;
                        case DenyType.Equals:
                            if (!Data.Headers.ContainsKey(head.Head))
                                break;
                            if (string.Equals(Data.Headers[head.Head].ToString(), head.Value,
                                StringComparison.OrdinalIgnoreCase))
                                return false;
                            break;
                        case DenyType.Like:
                            if (!Data.Headers.ContainsKey(head.Head))
                                break;
                            if (Data.Headers[head.Head].ToString().Contains(head.Value))
                                return false;
                            break;
                        case DenyType.Regex:
                            if (!Data.Headers.ContainsKey(head.Head))
                                break;
                            var regx = new Regex(head.Value,
                                RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Multiline);
                            if (regx.IsMatch(Data.Headers[head.Head].ToString()))
                                return false;
                            break;
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                LogRecorder.Exception(e);
                return true;
            }
        }

        #endregion
    }
}