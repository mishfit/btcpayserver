using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Models.AppViewModels;
using BTCPayServer.Plugins.Crowdfund;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Plugins.PointOfSale.Models;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using ExchangeSharp;
using Ganss.XSS;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitpayClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using PosViewType = BTCPayServer.Plugins.PointOfSale.PosViewType;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Services.Apps
{
    public class AppService
    {
        private readonly Dictionary<string, AppBaseType> _appTypes;
        readonly ApplicationDbContextFactory _ContextFactory;
        private readonly InvoiceRepository _InvoiceRepository;
        readonly CurrencyNameTable _Currencies;
        private readonly DisplayFormatter _displayFormatter;
        private readonly StoreRepository _storeRepository;
        private readonly HtmlSanitizer _HtmlSanitizer;
        public CurrencyNameTable Currencies => _Currencies;
        public AppService(
            IEnumerable<AppBaseType> apps,
            ApplicationDbContextFactory contextFactory,
            InvoiceRepository invoiceRepository,
            CurrencyNameTable currencies,
            DisplayFormatter displayFormatter,
            StoreRepository storeRepository,
            HtmlSanitizer htmlSanitizer)
        {
            _appTypes = apps.ToDictionary(a => a.Type, a => a);
            _ContextFactory = contextFactory;
            _InvoiceRepository = invoiceRepository;
            _Currencies = currencies;
            _storeRepository = storeRepository;
            _HtmlSanitizer = htmlSanitizer;
            _displayFormatter = displayFormatter;
        }
#nullable enable
        public Dictionary<string, string> GetAvailableAppTypes()
        {
            return _appTypes.ToDictionary(app => app.Key, app => app.Value.Description);
        }

        public AppBaseType? GetAppType(string appType)
        {
            _appTypes.TryGetValue(appType, out var a);
            return a;
        }

        public async Task<object?> GetInfo(string appId)
        {
            var appData = await GetApp(appId, null);
            if (appData is null)
                return null;
            var appType = GetAppType(appData.AppType);
            if (appType is null)
                return null;
            return appType.GetInfo(appData);
        }

        public async Task<IEnumerable<ItemStats>> GetItemStats(AppData appData)
        {
            if (GetAppType(appData.AppType) is not IHasItemStatsAppType salesType)
                throw new InvalidOperationException("This app isn't a SalesAppBaseType");
            var paidInvoices = await GetInvoicesForApp(_InvoiceRepository, appData,
                null, new[]
                {
                    InvoiceState.ToString(InvoiceStatusLegacy.Paid),
                    InvoiceState.ToString(InvoiceStatusLegacy.Confirmed),
                    InvoiceState.ToString(InvoiceStatusLegacy.Complete)
                });
            return await salesType.GetItemStats(appData, paidInvoices);
        }

        public static Task<SalesStats> GetSalesStatswithPOSItems(ViewPointOfSaleViewModel.Item[] items,
            InvoiceEntity[] paidInvoices, int numberOfDays)
        {
            var series = paidInvoices
                .Aggregate(new List<InvoiceStatsItem>(), AggregateInvoiceEntitiesForStats(items))
                .GroupBy(entity => entity.Date)
                .Select(entities => new SalesStatsItem
                {
                    Date = entities.Key,
                    Label = entities.Key.ToString("MMM dd", CultureInfo.InvariantCulture),
                    SalesCount = entities.Count()
                });

            // fill up the gaps
            foreach (var i in Enumerable.Range(0, numberOfDays))
            {
                var date = (DateTimeOffset.UtcNow - TimeSpan.FromDays(i)).Date;
                if (!series.Any(e => e.Date == date))
                {
                    series = series.Append(new SalesStatsItem
                    {
                        Date = date,
                        Label = date.ToString("MMM dd", CultureInfo.InvariantCulture)
                    });
                }
            }

            return Task.FromResult(new SalesStats
            {
                SalesCount = series.Sum(i => i.SalesCount),
                Series = series.OrderBy(i => i.Label)
            });
        }

        public async Task<SalesStats> GetSalesStats(AppData app, int numberOfDays = 7)
        {
            if (GetAppType(app.AppType) is not IHasSaleStatsAppType salesType)
                throw new InvalidOperationException("This app isn't a SalesAppBaseType");
            var paidInvoices = await GetInvoicesForApp(_InvoiceRepository, app, DateTimeOffset.UtcNow - TimeSpan.FromDays(numberOfDays),
                new[]
                {
                    InvoiceState.ToString(InvoiceStatusLegacy.Paid),
                    InvoiceState.ToString(InvoiceStatusLegacy.Confirmed),
                    InvoiceState.ToString(InvoiceStatusLegacy.Complete)
                });

            return await salesType.GetSalesStats(app, paidInvoices, numberOfDays);
        }

        public class InvoiceStatsItem
        {
            public string ItemCode { get; set; } = string.Empty;
            public decimal FiatPrice { get; set; }
            public DateTime Date { get; set; }
        }

        public static Func<List<InvoiceStatsItem>, InvoiceEntity, List<InvoiceStatsItem>> AggregateInvoiceEntitiesForStats(ViewPointOfSaleViewModel.Item[] items)
        {
            return (res, e) =>
            {
                if (e.Metadata.PosData != null)
                {
                    // flatten single items from POS data
                    var data = e.Metadata.PosData.ToObject<PosAppData>();
                    if (data is not { Cart.Length: > 0 })
                        return res;
                    foreach (var lineItem in data.Cart)
                    {
                        var item = items.FirstOrDefault(p => p.Id == lineItem.Id);
                        if (item == null)
                            continue;

                        for (var i = 0; i < lineItem.Count; i++)
                        {
                            res.Add(new InvoiceStatsItem
                            {
                                ItemCode = item.Id,
                                FiatPrice = lineItem.Price.Value,
                                Date = e.InvoiceTime.Date
                            });
                        }
                    }
                }
                else
                {
                    var fiatPrice = e.GetPayments(true).Sum(pay =>
                    {
                        var paymentMethodId = pay.GetPaymentMethodId();
                        var value = pay.GetCryptoPaymentData().GetValue() - pay.NetworkFee;
                        var rate = e.GetPaymentMethod(paymentMethodId).Rate;
                        return rate * value;
                    });
                    res.Add(new InvoiceStatsItem
                    {
                        ItemCode = e.Metadata.ItemCode,
                        FiatPrice = fiatPrice,
                        Date = e.InvoiceTime.Date
                    });
                }
                return res;
            };
        }

        public static string GetAppOrderId(AppData app) => GetAppOrderId(app.AppType, app.Id);
        public static string GetAppOrderId(string appType, string appId) =>
            appType switch
            {
                CrowdfundAppType.AppType => $"crowdfund-app_{appId}",
                PointOfSaleAppType.AppType => $"pos-app_{appId}",
                _ => $"{appType}_{appId}"
            };

        public static string GetAppInternalTag(string appId) => $"APP#{appId}";
        public static string[] GetAppInternalTags(InvoiceEntity invoice)
        {
            return invoice.GetInternalTags("APP#");
        }

        public static async Task<InvoiceEntity[]> GetInvoicesForApp(InvoiceRepository invoiceRepository, AppData appData, DateTimeOffset? startDate = null, string[]? status = null)
        {
            var invoices = await invoiceRepository.GetInvoices(new InvoiceQuery
            {
                StoreId = new[] { appData.StoreDataId },
                OrderId = appData.TagAllInvoices ? null : new[] { GetAppOrderId(appData) },
                Status = status ?? new[]{
                    InvoiceState.ToString(InvoiceStatusLegacy.New),
                    InvoiceState.ToString(InvoiceStatusLegacy.Paid),
                    InvoiceState.ToString(InvoiceStatusLegacy.Confirmed),
                    InvoiceState.ToString(InvoiceStatusLegacy.Complete)},
                StartDate = startDate
            });

            // Old invoices may have invoices which were not tagged
            invoices = invoices.Where(inv => appData.TagAllInvoices || inv.Version < InvoiceEntity.InternalTagSupport_Version ||
                                             inv.InternalTags.Contains(GetAppInternalTag(appData.Id))).ToArray();
            return invoices;
        }

        public async Task<StoreData[]> GetOwnedStores(string userId)
        {
            await using var ctx = _ContextFactory.CreateContext();
            return await ctx.UserStore
                .Where(us => us.ApplicationUserId == userId && us.Role == StoreRoles.Owner)
                .Select(u => u.StoreData)
                .ToArrayAsync();
        }

        public async Task<bool> DeleteApp(AppData appData)
        {
            await using var ctx = _ContextFactory.CreateContext();
            ctx.Apps.Add(appData);
            ctx.Entry(appData).State = EntityState.Deleted;
            return await ctx.SaveChangesAsync() == 1;
        }

        public async Task<ListAppsViewModel.ListAppViewModel[]> GetAllApps(string? userId, bool allowNoUser = false, string? storeId = null)
        {
            await using var ctx = _ContextFactory.CreateContext();
            var listApps = await ctx.UserStore
                .Where(us =>
                    (allowNoUser && string.IsNullOrEmpty(userId) || us.ApplicationUserId == userId) &&
                    (storeId == null || us.StoreDataId == storeId))
                .Join(ctx.Apps, us => us.StoreDataId, app => app.StoreDataId,
                    (us, app) =>
                        new ListAppsViewModel.ListAppViewModel
                        {
                            IsOwner = us.Role == StoreRoles.Owner,
                            StoreId = us.StoreDataId,
                            StoreName = us.StoreData.StoreName,
                            AppName = app.Name,
                            AppType = app.AppType,
                            Id = app.Id,
                            Created = app.Created,
                            App = app
                        })
                .OrderBy(b => b.Created)
                .ToArrayAsync();

            // allowNoUser can lead to apps being included twice, unify them with distinct
            if (allowNoUser)
            {
                listApps = listApps.DistinctBy(a => a.Id).ToArray();
            }

            foreach (ListAppsViewModel.ListAppViewModel app in listApps)
            {
                app.ViewStyle = GetAppViewStyle(app.App, app.AppType);
            }

            return listApps;
        }

        public string GetAppViewStyle(AppData app, string appType)
        {
            string style;
            switch (appType)
            {
                case PointOfSaleAppType.AppType:
                    var settings = app.GetSettings<PointOfSaleSettings>();
                    string posViewStyle = (settings.EnableShoppingCart ? PosViewType.Cart : settings.DefaultView).ToString();
                    style = typeof(PosViewType).DisplayName(posViewStyle);
                    break;

                default:
                    style = string.Empty;
                    break;
            }

            return style;
        }

        public async Task<List<AppData>> GetApps(string[] appIds, bool includeStore = false)
        {
            await using var ctx = _ContextFactory.CreateContext();
            var query = ctx.Apps
                .Where(app => appIds.Contains(app.Id));
            if (includeStore)
            {
                query = query.Include(data => data.StoreData);
            }
            return await query.ToListAsync();
        }

        public async Task<List<AppData>> GetApps(string appType)
        {
            await using var ctx = _ContextFactory.CreateContext();
            var query = ctx.Apps
                .Where(app => app.AppType == appType);
            return await query.ToListAsync();
        }

        public async Task<AppData?> GetApp(string appId, string? appType, bool includeStore = false)
        {
            await using var ctx = _ContextFactory.CreateContext();
            var query = ctx.Apps
                .Where(us => us.Id == appId &&
                             (appType == null || us.AppType == appType));

            if (includeStore)
            {
                query = query.Include(data => data.StoreData);
            }
            return await query.FirstOrDefaultAsync();
        }

        public Task<StoreData?> GetStore(AppData app)
        {
            return _storeRepository.FindStore(app.StoreDataId);
        }

        public string SerializeTemplate(ViewPointOfSaleViewModel.Item[] items)
        {
            var mappingNode = new YamlMappingNode();
            foreach (var item in items)
            {
                var itemNode = new YamlMappingNode();
                itemNode.Add("title", new YamlScalarNode(item.Title));
                if (item.Price.Type != ViewPointOfSaleViewModel.Item.ItemPrice.ItemPriceType.Topup && item.Price.Value is not null)
                    itemNode.Add("price", new YamlScalarNode(item.Price.Value.ToStringInvariant()));
                if (!string.IsNullOrEmpty(item.Description))
                {
                    itemNode.Add("description", new YamlScalarNode(item.Description)
                    {
                        Style = ScalarStyle.DoubleQuoted
                    });
                }
                if (!string.IsNullOrEmpty(item.Image))
                {
                    itemNode.Add("image", new YamlScalarNode(item.Image));
                }
                itemNode.Add("price_type", new YamlScalarNode(item.Price.Type.ToStringLowerInvariant()));
                itemNode.Add("disabled", new YamlScalarNode(item.Disabled.ToStringLowerInvariant()));
                if (item.Inventory.HasValue)
                {
                    itemNode.Add("inventory", new YamlScalarNode(item.Inventory.ToString()));
                }

                if (!string.IsNullOrEmpty(item.BuyButtonText))
                {
                    itemNode.Add("buyButtonText", new YamlScalarNode(item.BuyButtonText));
                }

                if (item.PaymentMethods?.Any() is true)
                {
                    itemNode.Add("payment_methods", new YamlSequenceNode(item.PaymentMethods.Select(s => new YamlScalarNode(s))));
                }
                mappingNode.Add(item.Id, itemNode);
            }

            var serializer = new SerializerBuilder().Build();
            return serializer.Serialize(mappingNode);
        }

        public ViewPointOfSaleViewModel.Item[] Parse(string template, string currency)
        {
            return Parse(_HtmlSanitizer, _displayFormatter, template, currency);
        }


        public ViewPointOfSaleViewModel.Item[] GetPOSItems(string template, string currency)
        {
            return GetPOSItems(_HtmlSanitizer, _displayFormatter, template, currency);
        }
        public static ViewPointOfSaleViewModel.Item[] Parse(HtmlSanitizer htmlSanitizer, DisplayFormatter displayFormatter, string template, string currency)
        {
            if (string.IsNullOrWhiteSpace(template))
                return Array.Empty<ViewPointOfSaleViewModel.Item>();
            using var input = new StringReader(template);
            YamlStream stream = new();
            stream.Load(input);
            var root = (YamlMappingNode)stream.Documents[0].RootNode;
            return root
                .Children
                .Select(kv => new PosHolder(htmlSanitizer) { Key = htmlSanitizer.Sanitize((kv.Key as YamlScalarNode)?.Value), Value = kv.Value as YamlMappingNode })
                .Where(kv => kv.Value != null)
                .Select(c =>
                {
                    ViewPointOfSaleViewModel.Item.ItemPrice price = new();
                    var pValue = c.GetDetail("price")?.FirstOrDefault();

                    switch (c.GetDetailString("custom") ?? c.GetDetailString("price_type")?.ToLowerInvariant())
                    {
                        case "topup":
                        case null when pValue is null:
                            price.Type = ViewPointOfSaleViewModel.Item.ItemPrice.ItemPriceType.Topup;
                            break;
                        case "true":
                        case "minimum":
                            price.Type = ViewPointOfSaleViewModel.Item.ItemPrice.ItemPriceType.Minimum;
                            if (pValue != null && !string.IsNullOrEmpty(pValue.Value?.Value))
                            {
                                price.Value = decimal.Parse(pValue.Value.Value, CultureInfo.InvariantCulture);
                                price.Formatted = displayFormatter.Currency(price.Value.Value, currency, DisplayFormatter.CurrencyFormat.Symbol);
                            }
                            break;
                        case "fixed":
                        case "false":
                        case null:
                            price.Type = ViewPointOfSaleViewModel.Item.ItemPrice.ItemPriceType.Fixed;
                            if (pValue?.Value.Value is not null)
                            {
                                price.Value = decimal.Parse(pValue.Value.Value, CultureInfo.InvariantCulture);
                                price.Formatted = displayFormatter.Currency(price.Value.Value, currency, DisplayFormatter.CurrencyFormat.Symbol);
                            }
                            break;
                    }

                    return new ViewPointOfSaleViewModel.Item
                    {
                        Description = c.GetDetailString("description"),
                        Id = c.Key,
                        Image = c.GetDetailString("image"),
                        Title = c.GetDetailString("title") ?? c.Key,
                        Price = price,
                        BuyButtonText = c.GetDetailString("buyButtonText"),
                        Inventory =
                            string.IsNullOrEmpty(c.GetDetailString("inventory"))
                                ? null
                                : int.Parse(c.GetDetailString("inventory"), CultureInfo.InvariantCulture),
                        PaymentMethods = c.GetDetailStringList("payment_methods"),
                        Disabled = c.GetDetailString("disabled") == "true"
                    };
                })
                .ToArray();
        }

        public static ViewPointOfSaleViewModel.Item[] GetPOSItems(HtmlSanitizer htmlSanitizer, DisplayFormatter displayFormatter, string template, string currency)
        {
            return Parse(htmlSanitizer, displayFormatter, template, currency).Where(c => !c.Disabled).ToArray();
        }
#nullable restore
        private class PosHolder
        {
            private readonly HtmlSanitizer _htmlSanitizer;

            public PosHolder(
                HtmlSanitizer htmlSanitizer)
            {
                _htmlSanitizer = htmlSanitizer;
            }

            public string Key { get; set; }
            public YamlMappingNode Value { get; set; }

            public IEnumerable<PosScalar> GetDetail(string field)
            {
                var res = Value.Children
                                 .Where(kv => kv.Value != null)
                                 .Select(kv => new PosScalar { Key = (kv.Key as YamlScalarNode)?.Value, Value = kv.Value as YamlScalarNode })
                                 .Where(cc => cc.Key == field);
                return res;
            }

            public string GetDetailString(string field)
            {
                var raw = GetDetail(field).FirstOrDefault()?.Value?.Value;
                return raw is null ? null : _htmlSanitizer.Sanitize(raw);
            }
            public string[] GetDetailStringList(string field)
            {
                if (!Value.Children.ContainsKey(field) || !(Value.Children[field] is YamlSequenceNode sequenceNode))
                {
                    return null;
                }
                return sequenceNode.Children.Select(node => (node as YamlScalarNode)?.Value).Where(s => s != null).Select(s => _htmlSanitizer.Sanitize(s)).ToArray();
            }
        }
        private class PosScalar
        {
            public string Key { get; set; }
            public YamlScalarNode Value { get; set; }
        }
#nullable enable
        public async Task<AppData?> GetAppDataIfOwner(string userId, string appId, string? type = null)
        {
            if (userId == null || appId == null)
                return null;
            await using var ctx = _ContextFactory.CreateContext();
            var app = await ctx.UserStore
                            .Where(us => us.ApplicationUserId == userId && us.Role == StoreRoles.Owner)
                            .SelectMany(us => us.StoreData.Apps.Where(a => a.Id == appId))
               .FirstOrDefaultAsync();
            if (app == null)
                return null;
            if (type != null && type != app.AppType)
                return null;
            return app;
        }

        public async Task UpdateOrCreateApp(AppData app)
        {
            await using var ctx = _ContextFactory.CreateContext();
            if (string.IsNullOrEmpty(app.Id))
            {
                app.Id = Encoders.Base58.EncodeData(RandomUtils.GetBytes(20));
                app.Created = DateTimeOffset.UtcNow;
                await ctx.Apps.AddAsync(app);
            }
            else
            {
                ctx.Apps.Update(app);
                ctx.Entry(app).Property(data => data.Created).IsModified = false;
                ctx.Entry(app).Property(data => data.Id).IsModified = false;
                ctx.Entry(app).Property(data => data.AppType).IsModified = false;
            }
            await ctx.SaveChangesAsync();
        }

        private static bool TryParseJson(string json, [MaybeNullWhen(false)] out JObject result)
        {
            result = null;
            try
            {
                result = JObject.Parse(json);
                return true;
            }
            catch
            {
                return false;
            }
        }
#nullable enable
        public static bool TryParsePosCartItems(JObject? posData, [MaybeNullWhen(false)] out Dictionary<string, int> cartItems)
        {
            cartItems = null;
            if (posData is null)
                return false;
            if (!posData.TryGetValue("cart", out var cartObject))
                return false;
            if (cartObject is null)
                return false;

            cartItems = new();
            foreach (var o in cartObject.OfType<JObject>())
            {
                var id = o.GetValue("id", StringComparison.InvariantCulture)?.ToString();
                if (id != null)
                {
                    var countStr = o.GetValue("count", StringComparison.InvariantCulture)?.ToString() ?? string.Empty;
                    if (int.TryParse(countStr, out var count))
                    {
                        cartItems.TryAdd(id, count);
                    }
                }
            }
            return true;
        }

        public async Task SetDefaultSettings(AppData appData, string defaultCurrency)
        {
            var app = GetAppType(appData.AppType);
            if (app is null)
            {
                appData.SetSettings(null);
            }
            else
            {
                await app.SetDefaultSettings(appData, defaultCurrency);
            }
        }

        public async Task<string?> ViewLink(AppData app)
        {
            var appType = GetAppType(app.AppType);
            return await appType?.ViewLink(app)!;
        }
#nullable restore
    }

    public class ItemStats
    {
        public string ItemCode { get; set; }
        public string Title { get; set; }
        public int SalesCount { get; set; }
        public decimal Total { get; set; }
        public string TotalFormatted { get; set; }
    }

    public class SalesStats
    {
        public int SalesCount { get; set; }
        public IEnumerable<SalesStatsItem> Series { get; set; }
    }

    public class SalesStatsItem
    {
        public DateTime Date { get; set; }
        public string Label { get; set; }
        public int SalesCount { get; set; }
    }
}
