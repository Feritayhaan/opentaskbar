using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services.Widgets;

public sealed record WeatherData(
    string City,
    double Temperature,
    double FeelsLike,
    int Humidity,
    double WindSpeed,
    double High,
    double Low,
    int WeatherCode,
    string ConditionText,
    string GlyphKey);

public sealed record MarketQuote(
    string Symbol,
    string DisplayName,
    double Price,
    double ChangePercent,
    string Currency);

public sealed record NewsItem(
    string Title,
    string Source,
    string Link,
    DateTimeOffset PublishedAt);

/// <summary>Fetches and caches weather (Open-Meteo), market quotes (Yahoo Finance), and headlines (Google News RSS).</summary>
public sealed class OnlineFeedsService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private WeatherData? _weather;
    private string? _weatherCityKey;
    private DateTime _weatherFetchedUtc;

    private IReadOnlyList<MarketQuote> _markets = [];
    private string? _marketsKey;
    private DateTime _marketsFetchedUtc;

    private IReadOnlyList<NewsItem> _news = [];
    private AppLanguage? _newsLang;
    private DateTime _newsFetchedUtc;

    public WeatherData? Weather => _weather;
    public IReadOnlyList<MarketQuote> Markets => _markets;
    public IReadOnlyList<NewsItem> News => _news;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OpenTaskbar/0.2");
        return client;
    }

    public async Task<WeatherData?> GetWeatherAsync(string? configuredCity, bool force = false)
    {
        var cityKey = (configuredCity ?? "").Trim();
        if (!force && _weather is not null && string.Equals(_weatherCityKey, cityKey, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - _weatherFetchedUtc < TimeSpan.FromMinutes(15))
        {
            return _weather;
        }

        try
        {
            double lat, lon;
            string cityName;
            if (cityKey.Length > 0)
            {
                var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(cityKey)}&count=1&language={(UiText.IsEnglish ? "en" : "tr")}";
                using var geoDoc = JsonDocument.Parse(await Http.GetStringAsync(geoUrl));
                if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                {
                    return _weather;
                }
                var first = results[0];
                lat = first.GetProperty("latitude").GetDouble();
                lon = first.GetProperty("longitude").GetDouble();
                cityName = first.GetProperty("name").GetString() ?? cityKey;
            }
            else
            {
                using var ipDoc = JsonDocument.Parse(await Http.GetStringAsync("https://get.geojs.io/v1/ip/geo.json"));
                var root = ipDoc.RootElement;
                lat = double.Parse(root.GetProperty("latitude").GetString() ?? "41.01", CultureInfo.InvariantCulture);
                lon = double.Parse(root.GetProperty("longitude").GetString() ?? "28.95", CultureInfo.InvariantCulture);
                cityName = root.TryGetProperty("city", out var c) && !string.IsNullOrWhiteSpace(c.GetString())
                    ? c.GetString()!
                    : "Istanbul";
            }

            var wxUrl = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");
            using var wxDoc = JsonDocument.Parse(await Http.GetStringAsync(wxUrl));
            var cur = wxDoc.RootElement.GetProperty("current");
            var daily = wxDoc.RootElement.GetProperty("daily");
            var code = cur.GetProperty("weather_code").GetInt32();
            var (conditionKey, glyphKey) = MapWeatherCode(code);

            _weather = new WeatherData(
                City: cityName,
                Temperature: cur.GetProperty("temperature_2m").GetDouble(),
                FeelsLike: cur.GetProperty("apparent_temperature").GetDouble(),
                Humidity: cur.GetProperty("relative_humidity_2m").GetInt32(),
                WindSpeed: cur.GetProperty("wind_speed_10m").GetDouble(),
                High: daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                Low: daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                WeatherCode: code,
                ConditionText: UiText.Get(conditionKey),
                GlyphKey: glyphKey);
            _weatherCityKey = cityKey;
            _weatherFetchedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Weather fetch failed: {0}", ex.Message);
        }

        return _weather;
    }

    public async Task<IReadOnlyList<MarketQuote>> GetMarketsAsync(IReadOnlyList<string> symbols, bool force = false)
    {
        var key = string.Join(",", symbols);
        if (!force && _markets.Count > 0 && string.Equals(_marketsKey, key, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - _marketsFetchedUtc < TimeSpan.FromMinutes(2))
        {
            return _markets;
        }

        try
        {
            var tasks = symbols.Select(FetchSingleQuoteAsync).ToArray();
            var quotes = (await Task.WhenAll(tasks)).OfType<MarketQuote>().ToList();
            if (quotes.Count > 0)
            {
                _markets = quotes;
                _marketsKey = key;
                _marketsFetchedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Markets fetch failed: {0}", ex.Message);
        }

        return _markets;
    }

    private static async Task<MarketQuote?> FetchSingleQuoteAsync(string symbol)
    {
        try
        {
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=1d";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
            var price = meta.GetProperty("regularMarketPrice").GetDouble();
            var prev = meta.TryGetProperty("chartPreviousClose", out var cp) ? cp.GetDouble()
                : meta.TryGetProperty("previousClose", out var p) ? p.GetDouble() : price;
            var changePct = prev > 0 ? (price - prev) / prev * 100.0 : 0.0;
            var currency = meta.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "";
            return new MarketQuote(symbol, FriendlySymbolName(symbol), price, changePct, currency);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(AppLanguage language, bool force = false)
    {
        if (!force && _news.Count > 0 && _newsLang == language
            && DateTime.UtcNow - _newsFetchedUtc < TimeSpan.FromMinutes(10))
        {
            return _news;
        }

        try
        {
            var url = language == AppLanguage.English
                ? "https://news.google.com/rss?hl=en-US&gl=US&ceid=US:en"
                : "https://news.google.com/rss?hl=tr&gl=TR&ceid=TR:tr";
            var xml = await Http.GetStringAsync(url);
            var doc = XDocument.Parse(xml);
            var items = doc.Descendants("item")
                .Take(6)
                .Select(item =>
                {
                    var rawTitle = item.Element("title")?.Value?.Trim() ?? "";
                    var source = item.Element("source")?.Value?.Trim() ?? "";
                    var title = rawTitle;
                    if (!string.IsNullOrEmpty(source) && rawTitle.EndsWith(" - " + source, StringComparison.Ordinal))
                    {
                        title = rawTitle[..^(source.Length + 3)];
                    }
                    var link = item.Element("link")?.Value?.Trim() ?? "";
                    _ = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pubDate);
                    return new NewsItem(title, source, link, pubDate);
                })
                .Where(n => !string.IsNullOrEmpty(n.Title) && !string.IsNullOrEmpty(n.Link))
                .ToList();

            if (items.Count > 0)
            {
                _news = items;
                _newsLang = language;
                _newsFetchedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("News fetch failed: {0}", ex.Message);
        }

        return _news;
    }

    private static string FriendlySymbolName(string symbol) => symbol.ToUpperInvariant() switch
    {
        "XU100.IS" => "BIST 100",
        "USDTRY=X" => "USD / TRY",
        "EURTRY=X" => "EUR / TRY",
        "GBPTRY=X" => "GBP / TRY",
        "GC=F" => UiText.IsEnglish ? "Gold" : "Altın (Ons)",
        "BTC-USD" => "Bitcoin",
        "ETH-USD" => "Ethereum",
        "^GSPC" => "S&P 500",
        "^IXIC" => "Nasdaq",
        _ => symbol.EndsWith(".IS", StringComparison.OrdinalIgnoreCase) ? symbol[..^3] : symbol
    };

    private static (string ConditionKey, string GlyphKey) MapWeatherCode(int code) => code switch
    {
        0 => ("WxClear", "IconSun"),
        1 => ("WxMostlyClear", "IconSun"),
        2 => ("WxPartlyCloudy", "IconCloudSun"),
        3 => ("WxCloudy", "IconCloud"),
        45 or 48 => ("WxFog", "IconCloud"),
        51 or 53 or 55 or 56 or 57 => ("WxDrizzle", "IconCloudRain"),
        61 or 63 or 80 or 81 => ("WxRain", "IconCloudRain"),
        65 or 66 or 67 or 82 => ("WxHeavyRain", "IconCloudRain"),
        71 or 73 or 75 or 77 or 85 or 86 => ("WxSnow", "IconSnow"),
        95 or 96 or 99 => ("WxStorm", "IconCloudLightning"),
        _ => ("WxPartlyCloudy", "IconCloudSun")
    };
}
