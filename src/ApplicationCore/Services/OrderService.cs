using Ardalis.GuardClauses;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Microsoft.Azure.ServiceBus;

namespace Microsoft.eShopWeb.ApplicationCore.Services
{
    public class OrderService : IOrderService
    {
        private readonly IAsyncRepository<Order> _orderRepository;
        private readonly IUriComposer _uriComposer;
        private readonly IAsyncRepository<Basket> _basketRepository;
        private readonly IAsyncRepository<CatalogItem> _itemRepository;

        private readonly string ReserveOrderUrl = "https://eshop-afs.azurewebsites.net/api/ReserveOrder?code=jzaYNrecZAYvePPvGKDOsYVh84v3Weqr1k9bsQOnC4KvLIObHwrvfw==";
        private readonly string DeliveryOrderUrl = "https://eshop-afs.azurewebsites.net/api/DeliveryOrder?code=wska9FyuGSXbRiyCXtQhfHwyOjm4o4u4qnLA8DeyblbDg6LVfQknjQ==";

        private readonly string ServiceBusUrl = "Endpoint=sb://eshop-service-bus.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=YAj4mThDRKRd5btWIxlSaHQVQqtV6E5tAGzHTyPIzRs=";
        private readonly string QueueName = "orderitems";

        public OrderService(IAsyncRepository<Basket> basketRepository,
            IAsyncRepository<CatalogItem> itemRepository,
            IAsyncRepository<Order> orderRepository,
            IUriComposer uriComposer)
        {
            _orderRepository = orderRepository;
            _uriComposer = uriComposer;
            _basketRepository = basketRepository;
            _itemRepository = itemRepository;
        }

        public async Task CreateOrderAsync(int basketId, Address shippingAddress)
        {
            var basketSpec = new BasketWithItemsSpecification(basketId);
            var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

            Guard.Against.NullBasket(basketId, basket);
            Guard.Against.EmptyBasketOnCheckout(basket.Items);

            var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
            var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

            var items = basket.Items.Select(basketItem =>
            {
                var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
                var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
                var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
                return orderItem;
            }).ToList();

            var order = new Order(basket.BuyerId, shippingAddress, items);

            //await ReserveOrder(items);

            await DeliveryOrder(items, shippingAddress);

            await ReserveOrderBySB(items);

            await _orderRepository.AddAsync(order);
        }

        public async Task ReserveOrder(List<OrderItem> items)
        {
            HttpClient client = new HttpClient();

            dynamic data = new System.Dynamic.ExpandoObject();
            data.Units = items.Select(s => new { CatalogItemId = s.ItemOrdered.CatalogItemId, Units = s.Units });

            var json_request = JsonSerializer.Serialize(data);
            
            var response = await client.PostAsync(ReserveOrderUrl, new StringContent(json_request, Encoding.UTF8, "application/json"));
            
            var text = await response.Content.ReadAsStringAsync();
        }

        public async Task DeliveryOrder(List<OrderItem> items, Address shippingAddress)
        {
            HttpClient client = new HttpClient();

            dynamic data = new System.Dynamic.ExpandoObject();
            data.ShippingAddress = shippingAddress;
            data.FinalPrice = items.Sum(s => s.UnitPrice * s.Units);
            data.Units = items.Select(s => new { CatalogItemId = s.ItemOrdered.CatalogItemId, Units = s.Units });

            var json_request = JsonSerializer.Serialize(data);

            var response = await client.PostAsync(DeliveryOrderUrl, new StringContent(json_request, Encoding.UTF8, "application/json"));

            var text = await response.Content.ReadAsStringAsync();
        }

        public async Task ReserveOrderBySB(List<OrderItem> items)
        {
            dynamic data = new System.Dynamic.ExpandoObject();
            data.Units = items.Select(s => new { CatalogItemId = s.ItemOrdered.CatalogItemId, Units = s.Units });

            var json_request = JsonSerializer.Serialize(data);

            var queueClient = new QueueClient(ServiceBusUrl, QueueName);

            var encodedMessage = new Message(Encoding.UTF8.GetBytes(json_request));
            await queueClient.SendAsync(encodedMessage);

            await queueClient.CloseAsync();
        }
    }
}
