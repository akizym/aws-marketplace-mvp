using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using System;
using System.Collections.Generic;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TriggerFulfillment;

public class Function
{
    private static readonly string paymentsTableName = Environment.GetEnvironmentVariable("PAYMENTS_TABLE")!;
    private static readonly string ordersTableName = Environment.GetEnvironmentVariable("ORDERS_TABLE")!;
        
    private static readonly string fullFillmentsTableName   = Environment.GetEnvironmentVariable("FULFILLMENT_TABLE")!;
    private static readonly string eventBusName      = Environment.GetEnvironmentVariable("EVENT_BUS_NAME")!;
        
    private readonly AmazonDynamoDBClient ddb = new();
    private readonly AmazonEventBridgeClient eventBridge = new();
    
    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var message in sqsEvent.Records)
        {
            var paymentInfo = JsonSerializer.Deserialize<PaymentInfo>(message.Body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // 1. Retrieve order info from Orders table
            var order = await GetOrderById(paymentInfo.OrderId);

            ArgumentNullException.ThrowIfNull(order, "Order not found");

            // Idempotency: skip if already fulfilled
            if (order.Status.Equals("Fulfilled", StringComparison.OrdinalIgnoreCase))
            {
                context.Logger.LogWarning($"Order {order.OrderId} already fulfilled. Skipping.");
                continue;
            }

            // 2. Mock: Get license code from external service (e.g., Steam)
            var licenseCode = await GetLicenseFromProvider(order.ProductId, order.CustomerEmail);

            // 3. Prepare fulfillment and update order in a ddb transaction
            try
            {
                await FulfillOrderTransaction(order, licenseCode, paymentInfo);
                context.Logger.LogLine($"Order {order.OrderId} fulfilled with license {licenseCode}");
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Failed to fulfill order {order.OrderId}: {ex.Message}");
                throw;
            }

            // 4. (Optional) Publish OrderFulfilled event (mocked here)
            await PublishOrderFulfilledEvent(order.OrderId, licenseCode, order.CustomerEmail);
        }
    }

    private async Task<Order?> GetOrderById(string orderId)
    {
        var resp = await ddb.GetItemAsync(ordersTableName, new Dictionary<string, AttributeValue>
        {
            ["orderId"] = new() { S = orderId }
        });

        if (resp.Item == null || resp.Item.Count == 0) return null;

        return new Order
        {
            OrderId = orderId,
            CustomerEmail = resp.Item.GetValueOrDefault("customerEmail")?.S,
            Status = resp.Item.GetValueOrDefault("status")?.S
        };
    }

    private async Task FulfillOrderTransaction(Order order, string licenseCode, PaymentInfo paymentInfo)
    {
        // Fulfillment table item
        var fulfillmentItem = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = new() { S = order.OrderId },
            ["paymentId"] = new() { S = paymentInfo.PaymentId },
            ["licenseKey"] = new() { S = licenseCode },
            ["fulfilledAt"] = new() { S = DateTime.UtcNow.ToString("o") },
            ["provider"] = new() { S = paymentInfo.Provider ?? "" },
            ["receiptUrl"] = new() { S = paymentInfo.ReceiptUrl ?? "" },
            ["transactionId"] = new() { S = paymentInfo.TransactionId ?? "" }
        };

        // Build the transaction request
        var transactItems = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = fullFillmentsTableName,
                    Item = fulfillmentItem
                }
            },
            new()
            {
                Update = new Update
                {
                    TableName = ordersTableName,
                    Key = new Dictionary<string, AttributeValue> { ["orderId"] = new() { S = order.OrderId } },
                    UpdateExpression = "SET #s = :newStatus",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":newStatus"] = new() { S = "Fulfilled" }
                    },
                    ConditionExpression = "#s <> :newStatus",
                }
            }
        };

        var request = new TransactWriteItemsRequest
        {
            TransactItems = transactItems
        };

        await ddb.TransactWriteItemsAsync(request);
    }

    private Task<string> GetLicenseFromProvider(string productId, string customerId)
    {
        // Mock call to external service
        return Task.FromResult("LICENSE-" + Guid.NewGuid());
    }

    private async Task PublishOrderFulfilledEvent(string orderId, string licenseCode, string email)
    {
        var eventDetail = new
        {
            OrderId = orderId,
            LicenseKey = licenseCode,
            EventTimestamp = DateTime.UtcNow,
            CustomerEmail = email,
            Url = $"https://example.com/activate/{licenseCode}",
        };

        var eventEntry = new PutEventsRequestEntry
        {
            Source = "market.orders",
            DetailType = "OrderFulfilled",
            Detail = JsonSerializer.Serialize(eventDetail),
            EventBusName = eventBusName
        };

        var response = await eventBridge.PutEventsAsync(new PutEventsRequest
        {
            Entries = [eventEntry]
        });
    }
}

// --- Supporting Models ---

public class PaymentInfo
{
    public string PaymentId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; }
    public string Provider { get; set; }
    public string ReceiptUrl { get; set; }
    public string TransactionId { get; set; } = string.Empty;
}

public class Order
{
    public string OrderId { get; set; }
    
    public string CustomerEmail { get; set; }
    public string ProductId { get; set; }
    public string Status { get; set; }
}
