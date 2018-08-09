using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.ComponentModel;
using System.Numerics;

namespace BoltToken
{
    public class BoltToken : SmartContract
    {
        // Events
        [DisplayName("transfer")]
        public static event Action<byte[], byte[], BigInteger> Transferred;

        [DisplayName("refund")]
        public static event Action<byte[], BigInteger> Refund;

        [DisplayName("burn")]
        public static event Action<byte[], BigInteger, byte[]> Burnt;

        // Constants
        private static readonly byte[] neoAssetID = { 155, 124, 255, 218, 166, 116, 190, 174, 15, 147, 14, 190, 96, 133, 175, 144, 147, 229, 254, 86, 179, 74, 92, 34, 12, 205, 207, 110, 252, 51, 111, 197 };
        private static readonly byte[] gasAssetID = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };
        private const ulong assetFactor = 100000000; // for neo and gas
        private const uint oneDay = 24 * 60 * 60; // 24 hrs in seconds

        // Token Settings
        public static string Name() => "Bolt Token";
        public static string Symbol() => "BOLT";
        public static byte Decimals() => 8;
        private const ulong factor = 100000000; // decided by Decimals()
        private static readonly byte[] Owner = "ANFDcXqVBBSTTukbkj8ATYWi6w5TYEQRtK".ToScriptHash();

        // ICO settings
        private const ulong presaleAmount = 800_000_000 * factor; // private sale amount
        private const ulong crowdsaleAmount = 200_000_000 * factor; // private sale amount
        private const ulong tier1Hardcap = 2_000_000 * factor;
        private const ulong tier2Hardcap = 1_000_000 * factor;

        // Helpers
        private static StorageContext Context() => Storage.CurrentContext;

        public static Object Main(string operation, params object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
                TransactionOutput[] references = tx.GetReferences();
                byte[] sender = new byte[0] { };
                foreach (TransactionOutput output in references)
                {
                    sender = output.ScriptHash;
                    if (sender == ExecutionEngine.ExecutingScriptHash) return Runtime.CheckWitness(Owner);
                }
                if (IndividualCapOf(sender) <= 0 || !HasSaleStarted()) return false;
                return true;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                if (operation == "totalSupply") return TotalSupply();
                if (operation == "name") return Name();
                if (operation == "symbol") return Symbol();
                if (operation == "decimals") return Decimals();
                if (operation == "deploy") return Deploy();
                if (operation == "saleStartTime") return SaleStartTime();
                if (operation == "balanceOf")
                {
                    if (args.Length != 1) return 0;
                    byte[] account = (byte[])args[0];
                    if (account.Length != 20) return 0;
                    return BalanceOf(account);
                }
                if (operation == "transfer")
                {
                    if (args.Length != 3) return false;
                    return Transfer((byte[])args[0], (byte[])args[1], (BigInteger)args[2], ExecutionEngine.CallingScriptHash);
                }
                if (operation == "mintTokens")
                {
                    return MintTokens(false);
                }
                if (operation == "burnTokens")
                {
                    if (args.Length != 3) return false;
                    return BurnTokens((byte[])args[0], (BigInteger)args[1], (byte[])args[1], ExecutionEngine.CallingScriptHash);
                }
                if (operation == "setSaleConfig")
                {
                    if (args.Length != 3) return false;
                    if (!Runtime.CheckWitness(Owner)) return false;
                    return SetSaleConfig((BigInteger)args[0], (BigInteger)args[1], (BigInteger)args[2]);
                }
                if (operation == "addToWhitelist")
                {
                    if (args.Length != 2) return false;
                    if (!Runtime.CheckWitness(Owner)) return false;
                    return AddToWhitelist((byte[])args[0], (string)args[1]);
                }
                if (operation == "removeFromWhitelist")
                {
                    if (args.Length != 2) return false;
                    if (!Runtime.CheckWitness(Owner)) return false;
                    return RemoveFromWhitelist((byte[])args[0], (string)args[1]);
                }
                if (operation == "isInWhitelist")
                {
                    if (args.Length != 2) return false;
                    return IsInWhitelist((byte[])args[0], (string)args[1]);
                }
                if (operation == "totalWhitelisted")
                {
                    if (args.Length != 1) return false;
                    var tier = (string)args[0];
                    if (tier != "1" || tier != "2") return false;
                    return WhitelistTotalKey(tier).AsBigInteger();
                }
                if (operation == "enableTransfers")
                {
                    if (!Runtime.CheckWitness(Owner)) return false;
                    if (!HasSaleEnded()) return false;
                    Storage.Put(Context(), "transfersEnabled", 1);
                }
                if (operation == "individualCapOf")
                {
                    if (args.Length != 1) return 0;
                    byte[] account = (byte[])args[0];
                    return IndividualCapOf(account);
                }
                if (operation == "exchangeRate")
                {
                    if (args.Length != 1) return 0;
                    return ExchangeRate((bool)args[0]);
                }
            }
            return false;
        }

        public static bool Deploy()
        {
            if (Storage.Get(Context(), "totalSupply").Length != 0) return false;
            Storage.Put(Context(), Owner, presaleAmount);
            Storage.Put(Context(), "totalSupply", presaleAmount);
            Transferred(null, Owner, presaleAmount);
            return true;
        }

        public static BigInteger TotalSupply()
        {
            return Storage.Get(Context(), "totalSupply").AsBigInteger();
        }

        public static BigInteger BalanceOf(byte[] address)
        {
            return Storage.Get(Context(), address).AsBigInteger();
        }

        public static bool Transfer(byte[] from, byte[] to, BigInteger amount, byte[] caller)
        {
            if (from.Length != 20 || to.Length != 20) return false;
            if (amount < 0) return false;
            if (!Runtime.CheckWitness(from) && !(from == caller && caller != ExecutionEngine.ExecutingScriptHash)) return false;
            if (!CanTransfer() && from != Owner) return false;

            if (from == to) return true;
            if (amount == 0) return true;

            BigInteger balance = Storage.Get(Context(), from).AsBigInteger();
            if (balance < amount) return false;

            if (balance == amount) Storage.Delete(Context(), from);
            else Storage.Put(Context(), from, balance - amount);

            BigInteger receiverBalance = Storage.Get(Context(), to).AsBigInteger();
            Storage.Put(Context(), to, receiverBalance + amount);
            Transferred(from, to, amount);
            return true;
        }

        private static bool SetSaleConfig(BigInteger saleStartTime, BigInteger tokensForOneNeo, BigInteger tokensForOneGas)
        {
            if (HasSaleStarted()) return false;

            Storage.Put(Context(), "saleStartTime", saleStartTime);
            Storage.Put(Context(), "tokensForOneNeo", tokensForOneNeo);
            Storage.Put(Context(), "tokensForOneGas", tokensForOneGas);

            return true;
        }

        private static bool MintTokens(bool useGas)
        {
            byte[] sender = GetSender(false);
            if (sender.Length == 0) return false;

            Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
            if (Storage.Get(Context(), "lastMintTxn") == tx.Hash) return false;
            ulong sentAmount = GetSentAssets(false);
            Storage.Put(Context(), "lastMintTxn", tx.Hash);
            if (!HasSaleStarted() || HasSaleEnded())
            {
                Refund(sender, sentAmount);
                return false;
            }
            ulong exchangeRate = ExchangeRate(false);
            BigInteger mintedAmount = GetMintedAmount(sender, sentAmount, exchangeRate);
            if (mintedAmount <= 0) return false;
            Storage.Put(Context(), sender, mintedAmount + BalanceOf(sender));
            Storage.Put(Context(), "totalSupply", mintedAmount + TotalSupply());
            Transferred(null, sender, mintedAmount);
            return true;
        }

        private static bool BurnTokens(byte[] address, BigInteger amount, byte[] data, byte[] caller)
        {
            if (data.Length != 20) return false;
            if (!Transfer(address, null, amount, caller)) return false;
            Storage.Put(Context(), "totalSupply", TotalSupply() - amount);
            Burnt(address, amount, data);
            return true;
        }

        private static ulong ExchangeRate(bool useGas)
        {
            if (useGas)
            {
                return (ulong)Storage.Get(Context(), "tokensForOneGas").AsBigInteger();
            }
            else
            {
                return (ulong)Storage.Get(Context(), "tokensForOneNeo").AsBigInteger();
            }
        }

        private static BigInteger GetMintedAmount(byte[] sender, ulong sentAmount, ulong exchangeRate)
        {
            BigInteger wanted = sentAmount * exchangeRate / assetFactor;
            BigInteger individualCap = IndividualCapOf(sender);

            // Refund if above limit
            if (wanted > individualCap)
            {
                Refund(sender, (wanted - individualCap) * assetFactor / exchangeRate);
                return individualCap;
            }
            return wanted;
        }

        private static byte[] GetSender(bool useGas)
        {
            Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
            TransactionOutput[] reference = tx.GetReferences();
            byte[] assetID = useGas ? gasAssetID : neoAssetID;
            foreach (TransactionOutput output in reference)
            {
                if (output.AssetId == assetID) return output.ScriptHash;
            }
            return new byte[] { };
        }

        private static ulong GetSentAssets(bool useGas)
        {
            Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
            byte[] assetID = useGas ? gasAssetID : neoAssetID;

            TransactionOutput[] outputs = tx.GetOutputs();
            ulong value = 0;
            foreach (TransactionOutput output in outputs)
            {
                if (output.ScriptHash == ExecutionEngine.ExecutingScriptHash && output.AssetId == assetID)
                {
                    value += (ulong)output.Value;
                }
            }
            return value;
        }

        private static BigInteger IndividualCapOf(byte[] sender)
        {
            BigInteger individualCap = 0;

            // Calculate cap based on tier
            bool inTier1 = IsInWhitelist(sender, "1");
            if (IsInWhitelist(sender, "1")) individualCap = tier1Hardcap;

            if (individualCap == 0)
            {
                if (IsInWhitelist(sender, "2")) individualCap = tier2Hardcap;
            }

            if (individualCap == 0)
            {
                return 0;
            }

            // Calculate cap based on stage
            BigInteger remainingHardCap = crowdsaleAmount + presaleAmount - TotalSupply();
            uint currentDuration = Runtime.Time - SaleStartTime();

            if (currentDuration < oneDay) // first 24 hrs
            {
                individualCap = individualCap - BalanceOf(sender);
            }
            else if (currentDuration < oneDay * 2) // next 24 hrs
            {
                individualCap = (individualCap * 5) - BalanceOf(sender); // increase limit to cap * 5
            }
            else if (currentDuration < oneDay * 3) // 48 - 72 hrs
            {
                individualCap = remainingHardCap; // no cap
            }
            else // > 72 hrs
            {
                return 0; // sale ended
            }

            // Check hard limits
            if (individualCap < 0) return 0;
            if (individualCap > remainingHardCap) return remainingHardCap;

            return individualCap;
        }

        private static bool AddToWhitelist(byte[] address, string tier)
        {
            if (address.Length != 20) return false;
            if (IsInWhitelist(address, tier)) return false;
            var totalKey = WhitelistTotalKey(tier);
            var prevTotal = Storage.Get(Context(), totalKey).AsBigInteger();
            Storage.Put(Context(), totalKey, prevTotal + 1);
            Storage.Put(Context(), WhitelistKey(address, tier), 1);
            return true;
        }

        private static bool RemoveFromWhitelist(byte[] address, string tier)
        {
            if (address.Length != 20) return false;
            if (!IsInWhitelist(address, tier)) return false;
            var totalKey = WhitelistTotalKey(tier);
            var prevTotal = Storage.Get(Context(), totalKey).AsBigInteger();
            Storage.Put(Context(), totalKey, prevTotal - 1);
            Storage.Delete(Context(), WhitelistKey(address, tier));
            return true;
        }
        
        private static bool IsInWhitelist(byte[] address, string tier)
        {
            return Storage.Get(Context(), WhitelistKey(address, tier)).Length > 0;
        }

        private static bool HasSaleStarted()
        {
            var saleStartTime = SaleStartTime();
            return saleStartTime != 0 && saleStartTime <= Runtime.Time;
        }

        private static bool HasSaleEnded()
        {
            var saleStartTime = SaleStartTime();
            return saleStartTime != 0 && saleStartTime + oneDay * 3 >= Runtime.Time;
        }

        private static bool CanTransfer()
        {
            return Storage.Get(Context(), "transfersEnabled").Length > 0;
        }

        private static uint SaleStartTime()
        {
            return (uint)Storage.Get(Context(), "saleStartTime").AsBigInteger();
        }

        // Keys
        private static byte[] BalanceKey(byte[] address) => "balanceOf".AsByteArray().Concat(address);
        private static byte[] MintedAmountKey(byte[] address) => "mintedAmount".AsByteArray().Concat(address);
        private static byte[] WhitelistKey(byte[] address, string tier) => ("whitelist" + tier).AsByteArray().Concat(address);
        private static byte[] WhitelistTotalKey(string tier) => ("totalWhitelisted" + tier).AsByteArray();
    }
}
