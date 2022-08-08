using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer
{
    public partial class BTCPayNetworkProvider
    {
        private readonly ILogger<BTCPayNetworkProvider> _logger;
        public void InitECash()
        {
            var nbxplorerNetwork = NBXplorerNetworkProvider.GetFromCryptoCode("XEC");

            if (nbxplorerNetwork == null)
            {
                _logger.LogError($"{nameof(nbxplorerNetwork)} is null.");
            }

            var defaultSettings = BTCPayDefaultSettings.GetDefaultSettings(NetworkType);
            if (defaultSettings == null)
            {
                _logger.LogError($"{nameof(defaultSettings)} is null.");
            }


            Add(new BTCPayNetwork()
            {
                CryptoCode = nbxplorerNetwork.CryptoCode,
                DisplayName = "eCash",
                BlockExplorerLink = NetworkType == ChainName.Mainnet ? "https://explorer.bitcoinabc.org/tx{0}" :
                                    "https://texplorer.bitcoinabc.org/tx/{0}",
                NBXplorerNetwork = nbxplorerNetwork,
                CryptoImagePath = "imlegacy/ecash.png",
                DefaultSettings = defaultSettings,
                CoinType = NetworkType == ChainName.Mainnet ? new KeyPath("899'") : new KeyPath("0'"),
                SupportRBF = false,
                SupportPayJoin = false,
                VaultSupported = false,
                DefaultRateRules = new[]
                {
                    "XEC_X = XEC_BTC * BTC_X",
                    "XEC_BTC = coingecko(XEC_BTC)"
                },
            });
        }
    }
}
