using Connect_API.Trading;
using Open_API_Library;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;

namespace Open_API_2._0_Sample
{
    public partial class Main : Form
    {
        private string _clientId = "2907_q9cSHpndYWc3C3cPcRY0TUMZKJGZ2dLp15admo7mTv5VnxVx4z";
        private string _clientSecret = "b6m1Z44CaW5cBXdfLc8zYASY3wOVYmmq3Kxml73fNKkCCs3cyn";
        private string _token = "Q5Zl0avxM5Q9gVVjbuAI9rk12XVeDcnGoBiu3JoG4ZQ";
        private string _apiHost = "demo.ctraderapi.com";

        private int _apiPort = 5035;
        private long _accountID = 3396736;

        /*private string _clientId = "185_Cmy5vh47ORewO95NsLCbz10Xn6RAzxFA13fgyg5xTKhzuxj7jr";
        private string _clientSecret = "JcE4vc5TvscRmtouoqMZS8TxJ31119beYt1TInP4tnOqhCBHL9";
        private string _token = "bCLrebRzU3Lhq3eXf6y7-Xls6yfVAJNqUwLFk0__yoM";
        private string _apiHost = "sandbox-tradeapi.spotware.com";
        private long _accountID = 102741;
        private int _apiPort = 5035;*/
        private TcpClient _tcpClient = new TcpClient();

        private SslStream _apiSocket;
        private static Queue _writeQueue = new Queue(); // not thread safe
        private static Queue _readQueue = new Queue(); // not thread safe
        private static Queue writeQueueSync = Queue.Synchronized(_writeQueue); // thread safe
        private static Queue readQueueSync = Queue.Synchronized(_readQueue); // thread safe
        private static volatile bool isShutdown;
        private static volatile bool isRestart;
        private static int MaxMessageSize = 1000000;
        private static long testOrderId = -1;
        private static long testPositionId = -1;
        private IList<ProtoOACtidTraderAccount> _accounts;
        private List<ProtoOATrader> _traders;
        private IList<ProtoOASymbol> _symbolById;
        private IList<ProtoOALightSymbol> _symbolList;
        private ProtoOAReconcileRes _reconcile_response = null;
        private IList<ProtoOALightSymbol> _symbolListForConversion = null;
        private ProtoOASpotEvent _spot_event = null;
        private bool _equityWorking = false;
        private long _lastBalance;

        struct Price
        {
            public Price(double x, double y)
            {
                bid = x;
                ask = y;
            }

            public double bid { get; }
            public double ask { get; }
        }

        private IDictionary<ProtoOALightSymbol, Price> _prices;

        public ProtoOALightSymbol GetLightSymbolById(long symbolId)
        {
            ProtoOALightSymbol ret = null;
            foreach (var symbol in _symbolList)
            {
                if (symbol.SymbolId == symbolId)
                {
                    ret = symbol;
                    break;
                }
            }
            return ret;
        }

        public Main()
        {
            _tcpClient = new TcpClient(_apiHost, _apiPort); ;
            _apiSocket = new SslStream(_tcpClient.GetStream(), false, new RemoteCertificateValidationCallback(ValidateServerCertificate), null);
            _apiSocket.AuthenticateAsClient(_apiHost);
            Thread tl = new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;
                try
                {
                    Listen(_apiSocket, readQueueSync);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Listener throws exception: {0}", e);
                }
            });
            tl.Start();
            InitializeComponent();
        }

        // listener thread
        private void Listen(SslStream sslStream, Queue messagesQueue)
        {
            isShutdown = false;
            while (!isShutdown)
            {
                Thread.Sleep(1);

                byte[] _length = new byte[sizeof(int)];
                int readBytes = 0;
                do
                {
                    Thread.Sleep(0);
                    readBytes += sslStream.Read(_length, readBytes, _length.Length - readBytes);
                } while (readBytes < _length.Length);

                int length = BitConverter.ToInt32(_length.Reverse().ToArray(), 0);
                if (length <= 0)
                    continue;

                if (length > MaxMessageSize)
                {
                    string exceptionMsg = "Message length " + length.ToString() + " is out of range (0 - " + MaxMessageSize.ToString() + ")";
                    throw new System.IndexOutOfRangeException();
                }

                byte[] _message = new byte[length];
                readBytes = 0;
                do
                {
                    Thread.Sleep(0);
                    readBytes += sslStream.Read(_message, readBytes, _message.Length - readBytes);
                } while (readBytes < length);
                var msgFactory = new OpenApiMessagesFactory();
                var protoMessage = msgFactory.GetMessage(_message);
                messagesQueue.Enqueue("Received: " + OpenApiMessagesPresentation.ToString(protoMessage));
                if ((ProtoOAPayloadType)protoMessage.PayloadType == ProtoOAPayloadType.PROTO_OA_DEAL_LIST_RES)
                {
                    txtAccountInfo.Text += OpenApiMessagesPresentation.ToString(protoMessage);
                    txtAccountInfo.Text += Environment.NewLine;
                }
                switch ((ProtoOAPayloadType)protoMessage.PayloadType)
                {
                    case ProtoOAPayloadType.PROTO_OA_EXECUTION_EVENT:
                        var _payload_msg = msgFactory.GetExecutionEvent(_message);
                        if (_payload_msg.HasOrder)
                        {
                            testOrderId = _payload_msg.Order.OrderId;
                        }
                        if (_payload_msg.HasPosition)
                        {
                            testPositionId = _payload_msg.Position.PositionId;
                        }
                        break;

                    case ProtoOAPayloadType.PROTO_OA_GET_ACCOUNTS_BY_ACCESS_TOKEN_RES:
                        var _accounts_list = ProtoOAGetAccountListByAccessTokenRes.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        _accounts = _accounts_list.CtidTraderAccountList;

                        break;

                    case ProtoOAPayloadType.PROTO_OA_TRADER_RES:
                        var trader = ProtoOATraderRes.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        _traders.Add(trader.Trader);
                        break;
                    case ProtoOAPayloadType.PROTO_OA_DEAL_LIST_RES:
                        var deal_list = ProtoOADealListRes.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        for (var i = deal_list.DealList.Count - 1; i >= 0; i--)
                        {
                            var deal = deal_list.DealList[i];
                            if (deal.HasClosePositionDetail == false) continue;
                            _lastBalance = deal.ClosePositionDetail.Balance;
                            break;
                        }
                        break;
                    case ProtoOAPayloadType.PROTO_OA_SYMBOL_BY_ID_RES:
                        var symbols= ProtoOASymbolByIdRes.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        _symbolById = symbols.SymbolList;
                        break;
                    case ProtoOAPayloadType.PROTO_OA_SYMBOLS_LIST_RES:
                        var symbols_list = ProtoOASymbolsListRes.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        _symbolList = symbols_list.SymbolList;
                        _equityWorking = true;
                        break;
                    case ProtoOAPayloadType.PROTO_OA_RECONCILE_RES:
                        _reconcile_response = ProtoOAReconcileRes.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        break;
                    case ProtoOAPayloadType.PROTO_OA_SPOT_EVENT:
                        _spot_event = ProtoOASpotEvent.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        break;
                    case ProtoOAPayloadType.PROTO_OA_SYMBOLS_FOR_CONVERSION_RES:
                        var symbols_conversion_response = ProtoOASymbolsForConversionRes.CreateBuilder().MergeFrom(protoMessage.Payload).Build();
                        _symbolListForConversion = symbols_conversion_response.SymbolList;
                        break;
                    default:
                        break;
                };
            }
        }

        private void Transmit(ProtoMessage msg, bool log = true)
        {
            if (log)
            {
                txtMessages.Text += "Send: " + OpenApiMessagesPresentation.ToString(msg);
                txtMessages.Text += Environment.NewLine;
            }
            var msgByteArray = msg.ToByteArray();
            byte[] length = BitConverter.GetBytes(msgByteArray.Length).Reverse().ToArray();
            _apiSocket.Write(length);
            _apiSocket.Write(msgByteArray);
        }

        private void btnAuthorizeApplication_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateAppAuthorizationRequest(_clientId, _clientSecret);
            Transmit(msg);
        }

        private void Main_Load(object sender, EventArgs e)
        {
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;
            Console.WriteLine("Certificate error: {0}", sslPolicyErrors);
            return false;
        }

        private void btnGetAccountsList_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateAccountListRequest(_token);
            Transmit(msg);

            while (_accounts == null)
            {
                Thread.Sleep(100);
            }
            _traders = new List<ProtoOATrader>();
            foreach (var account in _accounts)
            {                   
                if (!account.IsLive)
                {
                    var authMsg = msgFactory.CreateAccAuthorizationRequest(_token, (long)account.CtidTraderAccountId);
                    Transmit(authMsg);
                    Thread.Sleep(500);
                    var traderMsg = msgFactory.CreateTraderRequest((long)account.CtidTraderAccountId);                   
                    Transmit(traderMsg);                              
                }
            }
            Thread.Sleep(1000);
            foreach (var trader in _traders)
            {
                cbAccounts.Items.Add(trader.CtidTraderAccountId);                
            }
            _accounts = null;
        }

        private void btnAuthorizeAccount_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateAccAuthorizationRequest(_token, _accountID);
            Transmit(msg);
        }

        private void btnSendMarketOrder_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateMarketOrderRequest(Convert.ToInt32(_accountID), _token, 1, ProtoOATradeSide.BUY, Convert.ToInt64(100000));
            Transmit(msg);
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (_readQueue.Count > 0)
            {
                txtMessages.AppendText((string)_readQueue.Dequeue() + Environment.NewLine);
            }
        }

        private void btnSubscribeForSpots_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateSubscribeForSpotsRequest(_accountID, 1);
            Transmit(msg);
        }

        private void btnUnSubscribeFromSpots_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateUnsubscribeFromSpotsRequest(_accountID, 1);
            Transmit(msg);
        }

        private void btnGetDealsList_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            //    var msg = msgFactory.CreateDealsListRequest(_accountID, 1526342400, 1540000000000);
            var startDate = new DateTimeOffset(DateTime.Now.AddDays(-1));
            var now = new DateTimeOffset(DateTime.Now);
            var msg = msgFactory.CreateDealsListRequest(_accountID, startDate.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
            Transmit(msg);
        }

        private void btnGetOrders_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateReconcileRequest(_accountID);
            Transmit(msg);
        }

        private void btnGetTransactions_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateCashflowHistoryRequest(_accountID, ((DateTimeOffset)DateTime.Now.AddDays(-5)).ToUnixTimeMilliseconds(), ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds());
            Transmit(msg);
        }

        private void btnGetTrendbars_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateTrendbarsRequest(_accountID, 1, ((DateTimeOffset)DateTime.Now.AddDays(-35)).ToUnixTimeMilliseconds(), ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds(), ProtoOATrendbarPeriod.M1);
            Transmit(msg);
        }

        private void btnGetTickData_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateTickDataRequest(_accountID, 1, ((DateTimeOffset)DateTime.Now.AddDays(-35)).ToUnixTimeMilliseconds(), ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds(), ProtoOAQuoteType.BID, "EURUSD");
            Transmit(msg);
        }

        private void btnGetSymbols_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateSymbolsListRequest(_accountID);
            Transmit(msg);
        }

        private void btnGetProfile_Click(object sender, EventArgs e)
        {
        }

        private void btnSendStopOrder_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateStopOrderRequest(Convert.ToInt32(_accountID), _token, 1, ProtoOATradeSide.BUY, Convert.ToInt64(100000), 1.2);
            Transmit(msg);
        }

        private void btnSendLimitOrder_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateLimitOrderRequest(Convert.ToInt32(_accountID), _token, 1, ProtoOATradeSide.BUY, Convert.ToInt64(100000), 1.1);
            Transmit(msg);
        }

        private void btnSendStopLimitOrder_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateStopLimitOrderRequest(Convert.ToInt32(_accountID), _token, 1, ProtoOATradeSide.BUY, Convert.ToInt64(100000), 1.2, 5);
            Transmit(msg);
        }

        private void btnClosePosition_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateClosePositionRequest(Convert.ToInt32(_accountID), _token, Convert.ToInt64(txtPositionID.Text), Convert.ToInt64(txtVolume.Text));
            Transmit(msg);
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            txtMessages.Text = string.Empty;
        }

        private void btnAmentSLTP_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateAmendPositionStopLossTakeProfitRequest(Convert.ToInt32(_accountID), _token, Convert.ToInt64(txtPositionIDTPSL.Text), Convert.ToDouble(txtStopLoss.Text), Convert.ToDouble(txtTakeProfit.Text));
            Transmit(msg);
        }

        private void timer2_Tick(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateHeartbeatEvent();
            Transmit(msg, false);
        }
        private void cbAccounts_SelectedIndexChanged(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            _accountID = (long)cbAccounts.SelectedItem;
        }

        private void btnOrderHistory_Click(object sender, EventArgs e)
        {
            txtAccountInfo.Text = "";
            var msgFactory = new OpenApiMessagesFactory();
            var startDate = new DateTimeOffset(DateTime.Now.AddDays(-1));
            var now = new DateTimeOffset(DateTime.Now);
            var msg = msgFactory.CreateDealsListRequest(_accountID, startDate.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
            Transmit(msg);
        }

        private double GetPrice(ProtoOALightSymbol symbol, bool isBid = false)
        {
            Price result;

            if (_prices.TryGetValue(symbol, out result))
            {
                return isBid ? result.bid : result.ask;
            }

            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateSubscribeForSpotsRequest(_accountID, symbol.SymbolId);
            Transmit(msg);

            while (_prices.TryGetValue(symbol, out result))
            {
                Thread.Sleep(100);                
            }

            return isBid ? result.bid : result.ask;
        }

        private void timer3_Tick(object sender, EventArgs e)
        {
            if (_equityWorking == false)
                return;
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateReconcileRequest(_accountID);
            Transmit(msg);

            while (_reconcile_response == null)
            {
                Thread.Sleep(100);
            }

            foreach (var position in _reconcile_response.PositionList)
            {
                _symbolById = null;
                msg = msgFactory.CreateSymbolByIdRequest(_accountID, position.TradeData.SymbolId);
                Transmit(msg);

                while (_symbolById == null)
                {
                    Thread.Sleep(100);
                }

                var lightSymbol = GetLightSymbolById(_symbolById[0].SymbolId);
                double tickSize = 1 / Math.Pow(10, _symbolById[0].Digits);
                double pipSize = 1 / Math.Pow(10, _symbolById[0].PipPosition);
                double tickValue = tickSize;
                double pipValue;
                double pos;

                if (lightSymbol.QuoteAssetId != 2)
                {
                    _symbolListForConversion = null;
                    msg = msgFactory.CreateSymbolsForConversionReqeust(_accountID, lightSymbol.QuoteAssetId, 2);
                    Transmit(msg);

                    while (_symbolListForConversion == null)
                    {
                        Thread.Sleep(100);
                    }

                    long asset = lightSymbol.QuoteAssetId;
                    double rate = 1;

                    foreach (var symbol in _symbolListForConversion)
                    {
                        if (symbol.BaseAssetId == asset)
                        {
                            rate *= GetPrice(symbol) / 100000;
                            asset = symbol.QuoteAssetId;
                        }
                        else
                        {
                            rate /= GetPrice(symbol) / 100000;
                            asset = symbol.BaseAssetId;
                        }
                    }

                    tickValue = tickSize * rate;
                }

                pipValue = tickValue * (pipSize / tickSize);

                if (position.TradeData.TradeSide == ProtoOATradeSide.BUY)
                {
                    pos = position.Price - GetPrice(lightSymbol) / 100000;
                }
                else
                {
                    pos = GetPrice(lightSymbol, true) / 100000 - position.Price;
                }
                
                double pips = Math.Round(pos * Math.Pow(10, _symbolById[0].PipPosition), _symbolById[0].Digits - _symbolById[0].PipPosition);
                long volume = position.TradeData.Volume / 100;
                double grossProfit = pips * pipValue * volume;
                double positionSwapMonetary = position.Swap / Math.Pow(10, 2);
                double positionDoubleCommissionMonetary = (position.Commission * 2) / Math.Pow(10, 2);

                double netProfit = grossProfit + positionDoubleCommissionMonetary + positionSwapMonetary;

                txtAccountInfo.Text += "netProfit : " + netProfit.ToString();
                txtAccountInfo.Text += Environment.NewLine;
            }
        }

        private void btnSymbolCategory_Click(object sender, EventArgs e)
        {
            var msgFactory = new OpenApiMessagesFactory();
            var msg = msgFactory.CreateSymbolCategoryListRequest(_accountID);
            Transmit(msg);
        }
    }
}