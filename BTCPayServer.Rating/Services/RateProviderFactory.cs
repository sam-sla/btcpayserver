﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Rating;
using ExchangeSharp;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Services.Rates
{
    public class RateProviderFactory
    {
        class WrapperRateProvider : IRateProvider
        {
            private readonly IRateProvider _inner;
            public Exception Exception { get; private set; }
            public TimeSpan Latency { get; set; }
            public WrapperRateProvider(IRateProvider inner)
            {
                _inner = inner;
            }
            public async Task<ExchangeRates> GetRatesAsync(CancellationToken cancellationToken)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                try
                {
                    return await _inner.GetRatesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Exception = ex;
                    return new ExchangeRates();
                }
                finally
                {
                    Latency = DateTimeOffset.UtcNow - now;
                }
            }
        }
        public class QueryRateResult
        {
            public TimeSpan Latency { get; set; }
            public ExchangeRates ExchangeRates { get; set; }
            public ExchangeException Exception { get; internal set; }
        }
        public RateProviderFactory(IOptions<MemoryCacheOptions> cacheOptions,
                                   IHttpClientFactory httpClientFactory,
                                   CoinAverageSettings coinAverageSettings)
        {
            _httpClientFactory = httpClientFactory;
            _CoinAverageSettings = coinAverageSettings;
            _CacheOptions = cacheOptions;
            // We use 15 min because of limits with free version of bitcoinaverage
            CacheSpan = TimeSpan.FromMinutes(15.0);
            InitExchanges();
        }
        private IOptions<MemoryCacheOptions> _CacheOptions;
        TimeSpan _CacheSpan;
        public TimeSpan CacheSpan
        {
            get
            {
                return _CacheSpan;
            }
            set
            {
                _CacheSpan = value;
                InvalidateCache();
            }
        }
        public void InvalidateCache()
        {
            var cache = new MemoryCache(_CacheOptions);
            foreach (var provider in Providers.Select(p => p.Value as CachedRateProvider).Where(p => p != null))
            {
                provider.CacheSpan = CacheSpan;
                provider.MemoryCache = cache;
            }
            if (Providers.TryGetValue(CoinAverageRateProvider.CoinAverageName, out var coinAverage) && coinAverage is BackgroundFetcherRateProvider c)
            {
                c.RefreshRate = CacheSpan;
                c.ValidatyTime = CacheSpan + TimeSpan.FromMinutes(1.0);
            }
        }
        CoinAverageSettings _CoinAverageSettings;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Dictionary<string, IRateProvider> _DirectProviders = new Dictionary<string, IRateProvider>();
        public Dictionary<string, IRateProvider> Providers
        {
            get
            {
                return _DirectProviders;
            }
        }

        private void InitExchanges()
        {
            // We need to be careful to only add exchanges which OnGetTickers implementation make only 1 request
            Providers.Add("binance", new ExchangeSharpRateProvider("binance", new ExchangeBinanceAPI(), true));
            Providers.Add("bittrex", new ExchangeSharpRateProvider("bittrex", new ExchangeBittrexAPI(), true));
            Providers.Add("poloniex", new ExchangeSharpRateProvider("poloniex", new ExchangePoloniexAPI(), true));
            Providers.Add("hitbtc", new ExchangeSharpRateProvider("hitbtc", new ExchangeHitbtcAPI(), false));

            // Cryptopia is often not available
            // Disabled because of https://twitter.com/Cryptopia_NZ/status/1085084168852291586
            // Providers.Add("cryptopia", new ExchangeSharpRateProvider("cryptopia", new ExchangeCryptopiaAPI(), false));

            // Handmade providers
            Providers.Add(CoinAverageRateProvider.CoinAverageName, new CoinAverageRateProvider() { Exchange = CoinAverageRateProvider.CoinAverageName, HttpClient = _httpClientFactory?.CreateClient("EXCHANGE_COINAVERAGE"), Authenticator = _CoinAverageSettings });
            Providers.Add("kraken", new KrakenExchangeRateProvider() { HttpClient = _httpClientFactory?.CreateClient("EXCHANGE_KRAKEN") });
            Providers.Add("bylls", new ByllsRateProvider(_httpClientFactory?.CreateClient("EXCHANGE_BYLLS")));
            Providers.Add("ndax", new NdaxRateProvider(_httpClientFactory?.CreateClient("EXCHANGE_NDAX")));
            Providers.Add("bitbank", new BitbankRateProvider(_httpClientFactory?.CreateClient("EXCHANGE_BITBANK")));
            Providers.Add("bitpay", new BitpayRateProvider(_httpClientFactory?.CreateClient("EXCHANGE_BITPAY")));

            // Those exchanges make multiple requests when calling GetTickers so we remove them
            //DirectProviders.Add("gemini", new ExchangeSharpRateProvider("gemini", new ExchangeGeminiAPI()));
            //DirectProviders.Add("bitfinex", new ExchangeSharpRateProvider("bitfinex", new ExchangeBitfinexAPI()));
            //DirectProviders.Add("okex", new ExchangeSharpRateProvider("okex", new ExchangeOkexAPI()));
            //DirectProviders.Add("bitstamp", new ExchangeSharpRateProvider("bitstamp", new ExchangeBitstampAPI()));

            foreach (var provider in Providers.ToArray())
            {
                if (provider.Key == "cryptopia") // Shitty exchange, rate often unavailable, it spams the logs
                    continue;
                var prov = new BackgroundFetcherRateProvider(Providers[provider.Key]);
                if(provider.Key == CoinAverageRateProvider.CoinAverageName)
                {
                    prov.RefreshRate = CacheSpan;
                    prov.ValidatyTime = CacheSpan + TimeSpan.FromMinutes(1.0);
                }
                else
                {
                    prov.RefreshRate = TimeSpan.FromMinutes(1.0);
                    prov.ValidatyTime = TimeSpan.FromMinutes(5.0);
                }
                Providers[provider.Key] = prov;
            }

            var cache = new MemoryCache(_CacheOptions);
            foreach (var supportedExchange in GetSupportedExchanges())
            {
                if (!Providers.ContainsKey(supportedExchange.Key))
                {
                    var coinAverage = new CoinAverageRateProvider()
                    {
                        Exchange = supportedExchange.Key,
                        HttpClient = _httpClientFactory?.CreateClient(),
                        Authenticator = _CoinAverageSettings
                    };
                    var cached = new CachedRateProvider(supportedExchange.Key, coinAverage, cache)
                    {
                        CacheSpan = CacheSpan
                    };
                    Providers.Add(supportedExchange.Key, cached);
                }
            }
        }

        public CoinAverageExchanges GetSupportedExchanges()
        {
            CoinAverageExchanges exchanges = new CoinAverageExchanges();
            foreach (var exchange in _CoinAverageSettings.AvailableExchanges)
            {
                exchanges.Add(exchange.Value);
            }

            // Add other exchanges supported here
            exchanges.Add(new CoinAverageExchange(CoinAverageRateProvider.CoinAverageName, "Coin Average", $"https://apiv2.bitcoinaverage.com/indices/global/ticker/short"));
            exchanges.Add(new CoinAverageExchange("bylls", "Bylls", "https://bylls.com/api/price?from_currency=BTC&to_currency=CAD"));
            exchanges.Add(new CoinAverageExchange("ndax", "NDAX", "https://ndax.io/api/returnTicker"));
            exchanges.Add(new CoinAverageExchange("bitbank", "Bitbank", "https://public.bitbank.cc/prices"));

            return exchanges;
        }

        public async Task<QueryRateResult> QueryRates(string exchangeName, CancellationToken cancellationToken)
        {
            Providers.TryGetValue(exchangeName, out var directProvider);
            directProvider = directProvider ?? NullRateProvider.Instance;

            var wrapper = new WrapperRateProvider(directProvider);
            var value = await wrapper.GetRatesAsync(cancellationToken);
            return new QueryRateResult()
            {
                Latency = wrapper.Latency,
                ExchangeRates = value,
                Exception = wrapper.Exception != null ? new ExchangeException() { Exception = wrapper.Exception, ExchangeName = exchangeName } : null
            };
        }
    }
}
