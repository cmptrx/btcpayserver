﻿using BTCPayServer.Authentication;
using System.Reflection;
using System.Linq;
using Microsoft.Extensions.Logging;
using BTCPayServer.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitpayClient;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Models;
using Newtonsoft.Json;
using System.Globalization;
using NBitcoin;
using NBitcoin.DataEncoders;
using BTCPayServer.Filters;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NBitcoin.Payment;
using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using System.Security.Claims;
using BTCPayServer.Services;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Validations;

using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Routing;
using NBXplorer.DerivationStrategy;
using NBXplorer;

namespace BTCPayServer.Controllers
{
    public partial class InvoiceController : Controller
    {
        InvoiceRepository _InvoiceRepository;
        BTCPayWallet _Wallet;
        IRateProvider _RateProvider;
        private InvoiceWatcher _Watcher;
        StoreRepository _StoreRepository;
        UserManager<ApplicationUser> _UserManager;
        IFeeProviderFactory _FeeProviderFactory;
        private CurrencyNameTable _CurrencyNameTable;
        ExplorerClient _Explorer;
        EventAggregator _EventAggregator;
        BTCPayNetworkProvider _NetworkProvider;
        public InvoiceController(InvoiceRepository invoiceRepository,
            CurrencyNameTable currencyNameTable,
            UserManager<ApplicationUser> userManager,
            BTCPayWallet wallet,
            IRateProvider rateProvider,
            StoreRepository storeRepository,
            EventAggregator eventAggregator,
            InvoiceWatcherAccessor watcher,
            ExplorerClient explorerClient,
            BTCPayNetworkProvider networkProvider,
            IFeeProviderFactory feeProviderFactory)
        {
            _CurrencyNameTable = currencyNameTable ?? throw new ArgumentNullException(nameof(currencyNameTable));
            _Explorer = explorerClient ?? throw new ArgumentNullException(nameof(explorerClient));
            _StoreRepository = storeRepository ?? throw new ArgumentNullException(nameof(storeRepository));
            _InvoiceRepository = invoiceRepository ?? throw new ArgumentNullException(nameof(invoiceRepository));
            _Wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _RateProvider = rateProvider ?? throw new ArgumentNullException(nameof(rateProvider));
            _Watcher = (watcher ?? throw new ArgumentNullException(nameof(watcher))).Instance;
            _UserManager = userManager;
            _FeeProviderFactory = feeProviderFactory ?? throw new ArgumentNullException(nameof(feeProviderFactory));
            _EventAggregator = eventAggregator;
            _NetworkProvider = networkProvider;
        }


        internal async Task<DataWrapper<InvoiceResponse>> CreateInvoiceCore(Invoice invoice, StoreData store, string serverUrl, double expiryMinutes = 15)
        {
            var derivationStrategy = store.DerivationStrategy;
            var entity = new InvoiceEntity
            {
                InvoiceTime = DateTimeOffset.UtcNow,
                DerivationStrategy = derivationStrategy ?? throw new BitpayHttpException(400, "This store has not configured the derivation strategy")
            };
            var storeBlob = store.GetStoreBlob();
            Uri notificationUri = Uri.IsWellFormedUriString(invoice.NotificationURL, UriKind.Absolute) ? new Uri(invoice.NotificationURL, UriKind.Absolute) : null;
            if (notificationUri == null || (notificationUri.Scheme != "http" && notificationUri.Scheme != "https")) //TODO: Filer non routable addresses ?
                notificationUri = null;
            EmailAddressAttribute emailValidator = new EmailAddressAttribute();
            entity.ExpirationTime = entity.InvoiceTime.AddMinutes(expiryMinutes);
            entity.MonitoringExpiration = entity.ExpirationTime + TimeSpan.FromMinutes(storeBlob.MonitoringExpiration);
            entity.OrderId = invoice.OrderId;
            entity.ServerUrl = serverUrl;
            entity.FullNotifications = invoice.FullNotifications;
            entity.NotificationURL = notificationUri?.AbsoluteUri;
            entity.BuyerInformation = Map<Invoice, BuyerInformation>(invoice);
            //Another way of passing buyer info to support
            FillBuyerInfo(invoice.Buyer, entity.BuyerInformation);
            if (entity?.BuyerInformation?.BuyerEmail != null)
            {
                if (!EmailValidator.IsEmail(entity.BuyerInformation.BuyerEmail))
                    throw new BitpayHttpException(400, "Invalid email");
                entity.RefundMail = entity.BuyerInformation.BuyerEmail;
            }
            entity.ProductInformation = Map<Invoice, ProductInformation>(invoice);
            entity.RedirectURL = invoice.RedirectURL ?? store.StoreWebsite;
            entity.Status = "new";
            entity.SpeedPolicy = ParseSpeedPolicy(invoice.TransactionSpeed, store.SpeedPolicy);

            var queries = storeBlob.GetSupportedCryptoCurrencies()
                    .Select(n => _NetworkProvider.GetNetwork(n))
                    .Where(n => n != null)
                    .Select(network =>
                    {
                        return new
                        {
                            network = network,
                            getFeeRate = _FeeProviderFactory.CreateFeeProvider(network).GetFeeRateAsync(),
                            getRate = _RateProvider.GetRateAsync(invoice.Currency),
                            getAddress = _Wallet.ReserveAddressAsync(ParseDerivationStrategy(derivationStrategy, network))
                        };
                    });

            var cryptoDatas = new Dictionary<string, CryptoData>();
            foreach (var q in queries)
            {
                CryptoData cryptoData = new CryptoData();
                cryptoData.CryptoCode = q.network.CryptoCode;
                cryptoData.FeeRate = (await q.getFeeRate);
                cryptoData.TxFee = storeBlob.NetworkFeeDisabled ? Money.Zero : cryptoData.FeeRate.GetFee(100); // assume price for 100 bytes
                cryptoData.Rate = await q.getRate;
                cryptoData.DepositAddress = (await q.getAddress).ToString();

#pragma warning disable CS0618
                if (q.network.CryptoCode == "BTC")
                {
                    entity.TxFee = cryptoData.TxFee;
                    entity.Rate = cryptoData.Rate;
                    entity.DepositAddress = cryptoData.DepositAddress;
                }
#pragma warning restore CS0618
                cryptoDatas.Add(cryptoData.CryptoCode, cryptoData);
            }
            entity.SetCryptoData(cryptoDatas);
            entity.PosData = invoice.PosData;
            entity = await _InvoiceRepository.CreateInvoiceAsync(store.Id, entity, _NetworkProvider);
            _Watcher.Watch(entity.Id);
            var resp = entity.EntityToDTO(_NetworkProvider);
            return new DataWrapper<InvoiceResponse>(resp) { Facade = "pos/invoice" };
        }

        private SpeedPolicy ParseSpeedPolicy(string transactionSpeed, SpeedPolicy defaultPolicy)
        {
            if (transactionSpeed == null)
                return defaultPolicy;
            var mappings = new Dictionary<string, SpeedPolicy>();
            mappings.Add("low", SpeedPolicy.LowSpeed);
            mappings.Add("medium", SpeedPolicy.MediumSpeed);
            mappings.Add("high", SpeedPolicy.HighSpeed);
            if (!mappings.TryGetValue(transactionSpeed, out SpeedPolicy policy))
                policy = defaultPolicy;
            return policy;
        }

        private void FillBuyerInfo(Buyer buyer, BuyerInformation buyerInformation)
        {
            if (buyer == null)
                return;
            buyerInformation.BuyerAddress1 = buyerInformation.BuyerAddress1 ?? buyer.Address1;
            buyerInformation.BuyerAddress2 = buyerInformation.BuyerAddress2 ?? buyer.Address2;
            buyerInformation.BuyerCity = buyerInformation.BuyerCity ?? buyer.City;
            buyerInformation.BuyerCountry = buyerInformation.BuyerCountry ?? buyer.country;
            buyerInformation.BuyerEmail = buyerInformation.BuyerEmail ?? buyer.email;
            buyerInformation.BuyerName = buyerInformation.BuyerName ?? buyer.Name;
            buyerInformation.BuyerPhone = buyerInformation.BuyerPhone ?? buyer.phone;
            buyerInformation.BuyerState = buyerInformation.BuyerState ?? buyer.State;
            buyerInformation.BuyerZip = buyerInformation.BuyerZip ?? buyer.zip;
        }

        private DerivationStrategyBase ParseDerivationStrategy(string derivationStrategy, BTCPayNetwork network)
        {
            return new DerivationStrategyFactory(network.NBitcoinNetwork).Parse(derivationStrategy);
        }

        private TDest Map<TFrom, TDest>(TFrom data)
        {
            return JsonConvert.DeserializeObject<TDest>(JsonConvert.SerializeObject(data));
        }
    }
}
