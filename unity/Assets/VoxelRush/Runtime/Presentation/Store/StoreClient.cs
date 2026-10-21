using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Purchasing;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Networking.Online;

namespace VoxelRush.Presentation.Store
{
    /// <summary>
    /// Store purchases through Unity IAP 5 (ECON-003). A store order is confirmed only after the API verified the
    /// receipt and granted the item, so an unverified order stays pending in the store and is retried on the next
    /// launch (FetchPurchases). The API, not this client, decides ownership.
    /// </summary>
    public sealed class StoreClient
    {
        private readonly PlayerApi _api;
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private StoreController? _store;

        public StoreClient(PlayerApi api) => _api = api;

        public bool Ready { get; private set; }

        public string? Status { get; private set; }

        /// <summary>Raised after the API granted (or already owned) a purchased SKU's product.</summary>
        public event Action<string>? Granted;

        public async Task ConnectAsync(IEnumerable<string> productIds)
        {
            if (_store != null)
            {
                return;
            }

            _store = UnityIAPServices.StoreController();
            _store.OnPurchasePending += OnPending;
            _store.OnPurchaseFailed += failed => Status = failed.FailureReason == PurchaseFailureReason.UserCancelled ? null : $"Purchase failed: {failed.Details}";
            _store.OnProductsFetched += _ =>
            {
                Ready = true;
                // Re-delivers orders left unconfirmed by an earlier crash or a failed verification.
                _store.FetchPurchases();
            };
            _store.OnProductsFetchFailed += failed => Status = "Store products unavailable.";
            _store.ProcessPendingOrdersOnPurchasesFetched(true);
            try
            {
                await _store.Connect();
            }
            catch (Exception e)
            {
                Status = "Store unavailable.";
                Debug.LogWarning(LogEvent.Format("store_connect_failed", ("error", e.Message)));
                return;
            }

            var definitions = new List<ProductDefinition>();
            foreach (string id in productIds)
            {
                definitions.Add(new ProductDefinition(id, ProductType.NonConsumable));
            }

            _store.FetchProducts(definitions);
        }

        /// <summary>Localized price, or null before products load.</summary>
        public string? PriceOf(string productId) => _store?.GetProductById(productId)?.metadata.localizedPriceString;

        public void Buy(string productId)
        {
            if (_store == null || !Ready)
            {
                Status = "Store is still loading.";
                return;
            }

            Status = "Waiting for the store…";
            _store.PurchaseProduct(productId);
        }

        private async void OnPending(PendingOrder order)
        {
            IReadOnlyList<CartItem> items = order.CartOrdered.Items();
            string productId = items.Count > 0 ? items[0].Product.definition.id : string.Empty;
            if (!_inFlight.Add(order.Info.TransactionID))
            {
                return;
            }

            try
            {
                (PurchaseVerdict verdict, string? error) = order.Info.Apple != null
                    ? await _api.SubmitPurchaseAsync("apple", order.Info.Apple.jwsRepresentation, null, null)
                    : await _api.SubmitPurchaseAsync("google", null, productId, order.Info.Google?.PurchaseToken);
                Debug.Log(LogEvent.Format("store_purchase_verified", ("product", productId), ("verdict", verdict)));
                switch (verdict)
                {
                    case PurchaseVerdict.Granted:
                    case PurchaseVerdict.AlreadyOwned:
                        _store!.ConfirmPurchase(order);
                        Status = "Purchased!";
                        Granted?.Invoke(productId);
                        break;
                    case PurchaseVerdict.Pending:
                        Status = "Payment pending. It unlocks when the store completes it.";
                        break;
                    default:
                        // Left unconfirmed: the store refunds or re-delivers it; never granted locally.
                        Status = error;
                        break;
                }
            }
            finally
            {
                _inFlight.Remove(order.Info.TransactionID);
            }
        }
    }
}
