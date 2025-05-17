using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.Events.Targets;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.SQS;
using Constructs;
using EventBus = Amazon.CDK.AWS.Events.EventBus;

namespace Altusproj
{
    public class AltusprojStack : Stack
    {
        public AltusprojStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
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
            var bus = new EventBus(this, "AltusEventBus", new Amazon.CDK.AWS.Events.EventBusProps
            {
                EventBusName = "AltusEventBus"
            });
            
            Function CreateLambda(string name, string handler)
            {
                return new Function(this, $"{name}Lambda", new FunctionProps
                {
                    Runtime = Runtime.DOTNET_8,
                    Handler = handler,
                    Code = Code.FromAsset($"lambdas/{name}/publish"),
                    MemorySize = 512,
                    Timeout = Duration.Seconds(10),
                    Environment = new Dictionary<string, string>
                    {
                        ["ORDERS_TABLE"] = ordersTable.TableName,
                        ["PAYMENTS_TABLE"] = paymentsTable.TableName,
                        ["FULFILLMENT_TABLE"] = fulfillmentTable.TableName,
                        ["EVENT_BUS_NAME"] = bus.EventBusName,
                    }
                });
            }

            // 🔧 Lambda Functions

            var createOrderLambda = CreateLambda("CreateOrder", "CreateOrder::CreateOrder.Function::FunctionHandler");
            var processPaymentLambda = CreateLambda("ProcessPayment", "ProcessPayment::ProcessPayment.Function::FunctionHandler");
            // var triggerFulfillmentLambda = CreateLambda("TriggerFulfillment", "TriggerFulfillment::TriggerFulfillment.Function::FunctionHandler");


            
            
            // ✅ Grant Permissions

            ordersTable.GrantReadWriteData(createOrderLambda);
            paymentsTable.GrantReadWriteData(createOrderLambda);
            paymentsTable.GrantReadWriteData(processPaymentLambda);
            ordersTable.GrantReadWriteData(processPaymentLambda);
            
            bus.GrantPutEventsTo(processPaymentLambda);
            bus.GrantPutEventsTo(createOrderLambda);
            // fulfillmentTable.GrantReadWriteData(triggerFulfillmentLambda);

            // 🌐 API Gateway

            var api = new RestApi(this, "AltusApi", new RestApiProps
            {
                RestApiName = "Altus Order API",
                Description = "Handles orders and payments"
            });

            var orders = api.Root.AddResource("orders");
            orders.AddMethod("POST", new LambdaIntegration(createOrderLambda));

            var webhook = api.Root.AddResource("payment-webhook");
            webhook.AddMethod("POST", new LambdaIntegration(processPaymentLambda));

            var orderCreatedDlq = new Queue(this, "OrderCreatedDlq", new QueueProps
            {
                QueueName = "OrderCreatedDlq"
            });
            
            var orderCreatedQueue = new Queue(this, "OrderCreatedQueue", new QueueProps
            {
                QueueName = "OrderCreatedQueue",
                DeadLetterQueue = new DeadLetterQueue
                {
                    Queue = orderCreatedDlq,
                    MaxReceiveCount = 5 // After 5 failed receives, message goes to DLQ
                }
            });

            var orderCreatedRule = new Rule(this, "OrderCreatedRule", new RuleProps
            {
                EventBus = bus,
                EventPattern = new EventPattern
                {
                    Source = new[] { "market.orders" },
                    DetailType = new[] { "OrderCreated" }
                }
            });

            var emailSenderLambda = CreateLambda(
                "EmailSender",
                "EmailSender::EmailSender.Function::FunctionHandler"
            );
            emailSenderLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Actions = new[] { "ses:SendEmail", "ses:SendRawEmail" },
                Resources = new[] { "*" } // Or limit to your verified SES identity/ARN
            }));
            
            // Add SQS Queue as target
            orderCreatedRule.AddTarget(new SqsQueue(orderCreatedQueue, new SqsQueueProps
            {
                Message = RuleTargetInput.FromObject(new Dictionary<string, object>
                {
                    { "detail-type", EventField.FromPath("$.detail-type") },
                    { "detail", EventField.FromPath("$.detail") }
                })
            }));
            
            orderCreatedQueue.GrantConsumeMessages(emailSenderLambda);

            emailSenderLambda.AddEventSource(new SqsEventSource(orderCreatedQueue));
            
            
            // var rule = new Rule(this, "PaymentSucceededRule", new RuleProps
            // {
            //     EventBus = bus,
            //     EventPattern = new EventPattern
            //     {
            //         DetailType = new[] { "PaymentSucceeded" }
            //     }
            // });

            //rule.AddTarget(new Amazon.CDK.AWS.Events.Targets.LambdaFunction(triggerFulfillmentLambda));

        }
    }
}
