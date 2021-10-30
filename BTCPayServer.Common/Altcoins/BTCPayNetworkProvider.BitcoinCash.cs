using System.Collections.Generic;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer
{
    public partial class BTCPayNetworkProvider
    {
        public void InitBitcoinCash()
        {
            var nbxplorerNetwork = NBXplorerNetworkProvider.GetFromCryptoCode("BCH");
            Add(new BTCPayNetwork()
            {
                CryptoCode = nbxplorerNetwork.CryptoCode,
                DisplayName = "Bitcoin Cash",
                BlockExplorerLink = NetworkType == ChainName.Mainnet ? "https://blockchain.com/bitcoin-cash/transaction/{0}" :
                                    "https://www.blockchain.com/bch-testnet/tx/{0}",
                NBXplorerNetwork = nbxplorerNetwork,
                CryptoImagePath = "imlegacy/bitcoin-cash.png",
                DefaultSettings = BTCPayDefaultSettings.GetDefaultSettings(NetworkType),
                CoinType = NetworkType == ChainName.Mainnet ? new KeyPath("0'") : new KeyPath("1'"),
                SupportRBF = false,
                SupportPayJoin = false,
                VaultSupported = false,
                DefaultRateRules = new[]
                {
                    "BCH_X = BCH_BTC * BTC_X",
                    "BCH_BTC = kraken(BCH_BTC)"
                },
            });
        }
    }
}
