using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.IAM;
using Constructs;
using System.Collections.Generic;

namespace Altusproj
{
    public class AltusprojStack : Stack
    {
        public AltusprojStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // 🗃️ DynamoDB Tables

            var ordersTable = new Table(this, "OrdersTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "orderId", Type = AttributeType.STRING },
                BillingMode = BillingMode.PAY_PER_REQUEST,
                TableName = "Orders"
            });

            var paymentsTable = new Table(this, "PaymentsTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "paymentId", Type = AttributeType.STRING },
                BillingMode = BillingMode.PAY_PER_REQUEST,
                TableName = "Payments"
            });

            var fulfillmentTable = new Table(this, "FulfillmentTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "orderId", Type = AttributeType.STRING },
                BillingMode = BillingMode.PAY_PER_REQUEST,
                TableName = "Fulfillments"
            });

            // 🧠 Lambda Function Props Helper
            Function CreateLambda(string name, string handler)
            {
                return new Function(this, $"{name}Lambda", new FunctionProps
                {
                    Runtime = Runtime.DOTNET_8,
                    Handler = handler,
                    Code = Code.FromAsset($"lambdas/{name}"),
                    MemorySize = 512,
                    Timeout = Duration.Seconds(10),
                    Environment = new Dictionary<string, string>
                    {
                        ["ORDERS_TABLE"] = ordersTable.TableName,
                        ["PAYMENTS_TABLE"] = paymentsTable.TableName,
                        ["FULFILLMENT_TABLE"] = fulfillmentTable.TableName
                    }
                });
            }

            // 🔧 Lambda Functions

            var createOrderLambda = CreateLambda("CreateOrder", "CreateOrder::CreateOrder.Function::FunctionHandler");
            // var processPaymentLambda = CreateLambda("ProcessPayment", "ProcessPayment::ProcessPayment.Function::FunctionHandler");
            // var triggerFulfillmentLambda = CreateLambda("TriggerFulfillment", "TriggerFulfillment::TriggerFulfillment.Function::FunctionHandler");

            // ✅ Grant Permissions

            ordersTable.GrantReadWriteData(createOrderLambda);
            paymentsTable.GrantReadWriteData(createOrderLambda);
            // paymentsTable.GrantReadWriteData(processPaymentLambda);
            // ordersTable.GrantReadWriteData(processPaymentLambda);
            // fulfillmentTable.GrantReadWriteData(triggerFulfillmentLambda);

            // 🌐 API Gateway

            var api = new RestApi(this, "AltusApi", new RestApiProps
            {
                RestApiName = "Altus Order API",
                Description = "Handles orders and payments"
            });

            var orders = api.Root.AddResource("orders");
            orders.AddMethod("POST", new LambdaIntegration(createOrderLambda));

            // var webhook = api.Root.AddResource("payment-webhook");
            // webhook.AddMethod("POST", new LambdaIntegration(processPaymentLambda));

            // 📣 EventBridge Event Bus and Rule

            var bus = new EventBus(this, "AltusEventBus", new Amazon.CDK.AWS.Events.EventBusProps
            {
                EventBusName = "AltusEventBus"
            });

            var rule = new Rule(this, "PaymentSucceededRule", new RuleProps
            {
                EventBus = bus,
                EventPattern = new EventPattern
                {
                    DetailType = new[] { "PaymentSucceeded" }
                }
            });

            //rule.AddTarget(new Amazon.CDK.AWS.Events.Targets.LambdaFunction(triggerFulfillmentLambda));

            // 💳 Allow processPaymentLambda to put events on EventBridge
            //bus.GrantPutEventsTo(processPaymentLambda);
        }
    }
}
