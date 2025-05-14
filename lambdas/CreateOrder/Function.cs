using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

// Required for AWS Lambda .NET runtime
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace CreateOrder
{
    public class OrderRequest
    {
        public List<string> ItemIds { get; set; } = new();
        public string Currency { get; set; } = "EUR";
        public int TotalAmount { get; set; }
        public string PaymentType { get; set; } = "Stripe";
        public string CustomerEmail { get; set; } = string.Empty;
    }

    public class Function
    {
        private static readonly string _ordersTableName = Environment.GetEnvironmentVariable("ORDERS_TABLE")!;
        private static readonly string _paymentsTableName = Environment.GetEnvironmentVariable("PAYMENTS_TABLE")!;
        private static readonly AmazonDynamoDBClient _ddb = new();

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

            // Generate IDs and mock payment session
            var orderId = Guid.NewGuid().ToString();
            var (paymentId, paymentUrl) = await PaymentService
                .CreatePaymentSessionAsync(orderId, input.PaymentType, input.TotalAmount, input.Currency);

            // Build the DynamoDB items
            var orderItem = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = orderId },
                ["itemIds"] = new() { L = input.ItemIds.Select(i => new AttributeValue { S = i }).ToList() },
                ["currency"] = new() { S = input.Currency },
                ["totalAmount"] = new() { N = input.TotalAmount.ToString() },
                ["paymentType"] = new() { S = input.PaymentType },
                ["customerEmail"] = new() { S = input.CustomerEmail },
                ["paymentId"] = new() { S = paymentId },
                ["status"] = new() { S = "PendingPayment" },
                ["createdAt"] = new() { S = DateTime.UtcNow.ToString("o") }
            };

            var paymentItem = new Dictionary<string, AttributeValue>
            {
                ["paymentId"] = new() { S = paymentId },
                ["orderId"] = new() { S = orderId },
                ["paymentType"] = new() { S = input.PaymentType },
                ["status"] = new() { S = "Pending" },
                ["sessionUrl"] = new() { S = paymentUrl },
                ["createdAt"] = new() { S = DateTime.UtcNow.ToString("o") }
            };

            // Write to DynamoDB
            await _ddb.PutItemAsync(new PutItemRequest
            {
                TableName = _ordersTableName,
                Item = orderItem
            });

            await _ddb.PutItemAsync(new PutItemRequest
            {
                TableName = _paymentsTableName,
                Item = paymentItem
            });

            // Return response
            var responseBody = JsonSerializer.Serialize(new
            {
                message = "Order created",
                orderId,
                paymentUrl
            });

            return new APIGatewayProxyResponse
            {
                StatusCode = 201,
                Body = responseBody,
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                }
            };
        }

        private static APIGatewayProxyResponse BadRequest(string msg) =>
            new()
            {
                StatusCode = 400,
                Body = JsonSerializer.Serialize(new { error = msg }),
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            };
    }
}