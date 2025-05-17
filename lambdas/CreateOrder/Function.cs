using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

// Required for AWS Lambda .NET runtime
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace CreateOrder
{
    public class OrderRequest
    {
        public string OrderId { get; set; } = string.Empty; 
        public List<string> ItemIds { get; set; } = new();
        public string Currency { get; set; } = "EUR";
        public int TotalAmount { get; set; }
        public string CustomerEmail { get; set; } = string.Empty;
        public string PaymentType { get; set; } = string.Empty;
    }

    public class Function
    {
        private static readonly string ordersTableName = Environment.GetEnvironmentVariable("ORDERS_TABLE")!;
        private static readonly string paymentsTableName = Environment.GetEnvironmentVariable("PAYMENTS_TABLE")!;
        private static readonly string eventBusName = Environment.GetEnvironmentVariable("EVENT_BUS_NAME")!;
        
        private readonly IAmazonDynamoDB ddb = new AmazonDynamoDBClient();

        private readonly IAmazonEventBridge eventBridge = new AmazonEventBridgeClient();

        public async Task<APIGatewayProxyResponse> FunctionHandler(
            APIGatewayProxyRequest request,
            ILambdaContext context)
        {
            if (string.IsNullOrEmpty(request.Body))
                return BadRequest("Missing request body.");

            OrderRequest? input;
            try
            {
                input = JsonSerializer.Deserialize<OrderRequest>(request.Body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Invalid JSON: {ex.Message}");
                return BadRequest("Invalid JSON.");
            }

            if (input == null || input.ItemIds.Count == 0 || input.TotalAmount <= 0)
                return BadRequest("Invalid order data.");

            input.OrderId = Guid.NewGuid().ToString();
            
            // Generate IDs and mock payment session
            var (paymentId, paymentUrl) = await PaymentService
                .CreatePaymentSessionAsync(input.OrderId, input.PaymentType);

            await SaveOrderAndPayment(paymentId, input);
            
            var eventEntry = new PutEventsRequestEntry
            {
                Source = "market.orders",
                DetailType = "OrderCreated",
                Detail = JsonSerializer.Serialize(input),
                EventBusName = eventBusName
            };

            await eventBridge.PutEventsAsync(new PutEventsRequest
            {
                Entries = [eventEntry]
            });
            
            // Return response
            var responseBody = JsonSerializer.Serialize(new
            {
                message = "Order created",
                input.OrderId,
                status = "PendingPayment",
                paymentUrl,
                paymentId,
            });

            return Ok(responseBody);
        }

        private async Task SaveOrderAndPayment(string paymentId, OrderRequest input)
        {
            // Build the DynamoDB items
            var orderItem = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = input.OrderId },
                ["itemIds"] = new() { L = input.ItemIds.Select(i => new AttributeValue { S = i }).ToList() },
                ["currency"] = new() { S = input.Currency },
                ["totalAmount"] = new() { N = input.TotalAmount.ToString() },
                ["customerEmail"] = new() { S = input.CustomerEmail },
                ["status"] = new() { S = "PendingPayment" },
                ["createdAt"] = new() { S = DateTime.UtcNow.ToString("o") }
            };

            var paymentItem = new Dictionary<string, AttributeValue>
            {
                ["paymentId"] = new() { S = paymentId },
                ["orderId"] = new() { S = input.OrderId },
                ["paymentType"] = new() { S = input.PaymentType },
                ["status"] = new() { S = "Pending" },
                //["sessionUrl"] = new() { S = paymentUrl },
                ["createdAt"] = new() { S = DateTime.UtcNow.ToString("o") }
            };
            
            Put putOrder = new()
            {
                TableName = ordersTableName,
                Item = orderItem,
            };
            
            Put putPayment = new()
            {
                TableName = paymentsTableName,
                Item = paymentItem
            };
            
            var transactRequest = new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new() { Put = putOrder },
                    // Update order status
                    new() { Put = putPayment }
                ]
            };
            await ddb.TransactWriteItemsAsync(transactRequest);

        }
        private static APIGatewayProxyResponse BadRequest(string msg) =>
            new()
            {
                StatusCode = 400,
                Body = JsonSerializer.Serialize(new { error = msg }),
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            };        
        
        private static APIGatewayProxyResponse Ok(string responseBody) =>
            new()
            {
                StatusCode = 201,
                Body = responseBody,
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                }
            };
    }
}