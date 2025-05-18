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
        public AltusprojStack(Construct scope, string id, IStackProps props = null) 
            : base(scope, id, props)
        {
            // ───────────────────────────────────────────────────────────
            // 1. DynamoDB Tables
            // ───────────────────────────────────────────────────────────
            var ordersTable = new Table(this, "OrdersTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "orderId", Type = AttributeType.STRING },
                BillingMode  = BillingMode.PAY_PER_REQUEST,
                TableName    = "Orders"
            });

            var paymentsTable = new Table(this, "PaymentsTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "paymentId", Type = AttributeType.STRING },
                BillingMode  = BillingMode.PAY_PER_REQUEST,
                TableName    = "Payments"
            });

            var fulfillmentTable = new Table(this, "FulfillmentTable", new TableProps
            {
                PartitionKey = new Attribute { Name = "orderId", Type = AttributeType.STRING },
                BillingMode  = BillingMode.PAY_PER_REQUEST,
                TableName    = "Fulfillments"
            });


            // ───────────────────────────────────────────────────────────
            // 2. Event Bus
            // ───────────────────────────────────────────────────────────
            var bus = new EventBus(this, "AltusEventBus", new Amazon.CDK.AWS.Events.EventBusProps
            {
                EventBusName = "AltusEventBus"
            });


            // ───────────────────────────────────────────────────────────
            // 3. Lambda Factory
            // ───────────────────────────────────────────────────────────
            Function CreateLambda(string name, string handler) =>
                new Function(this, $"{name}Lambda", new FunctionProps
                {
                    Runtime    = Runtime.DOTNET_8,
                    Handler    = handler,
                    Code       = Code.FromAsset($"lambdas/{name}/publish"),
                    MemorySize = 512,
                    Timeout    = Duration.Seconds(10),
                    Environment = new Dictionary<string, string>
                    {
                        ["ORDERS_TABLE"]      = ordersTable.TableName,
                        ["PAYMENTS_TABLE"]    = paymentsTable.TableName,
                        ["FULFILLMENT_TABLE"] = fulfillmentTable.TableName,
                        ["EVENT_BUS_NAME"]    = bus.EventBusName,
                    }
                });


            // ───────────────────────────────────────────────────────────
            // 4. Lambda Functions
            // ───────────────────────────────────────────────────────────
            var createOrderLambda      = CreateLambda("CreateOrder",       "CreateOrder::CreateOrder.Function::FunctionHandler");
            var processPaymentLambda   = CreateLambda("ProcessPayment",    "ProcessPayment::ProcessPayment.Function::FunctionHandler");
            var triggerFulfillmentLambda = CreateLambda("TriggerFulfillment", "TriggerFulfillment::TriggerFulfillment.Function::FunctionHandler");


            // ───────────────────────────────────────────────────────────
            // 5. Permissions (Dynamo & EventBus)
            // ───────────────────────────────────────────────────────────
            ordersTable.GrantReadWriteData(createOrderLambda);
            paymentsTable.GrantReadWriteData(createOrderLambda);

            paymentsTable.GrantReadWriteData(processPaymentLambda);
            ordersTable.GrantReadWriteData(processPaymentLambda);

            ordersTable.GrantReadWriteData(triggerFulfillmentLambda);
            paymentsTable.GrantReadData(triggerFulfillmentLambda);
            fulfillmentTable.GrantReadWriteData(triggerFulfillmentLambda);

            bus.GrantPutEventsTo(createOrderLambda);
            bus.GrantPutEventsTo(processPaymentLambda);
            bus.GrantPutEventsTo(triggerFulfillmentLambda);


            // ───────────────────────────────────────────────────────────
            // 6. API Gateway
            // ───────────────────────────────────────────────────────────
            var api = new RestApi(this, "AltusApi", new RestApiProps
            {
                RestApiName = "Altus Order API",
                Description = "Handles orders and payments"
            });

            var orders = api.Root.AddResource("orders");
            orders.AddMethod("POST", new LambdaIntegration(createOrderLambda));

            var webhook = api.Root.AddResource("payment-webhook");
            webhook.AddMethod("POST", new LambdaIntegration(processPaymentLambda));


            // ───────────────────────────────────────────────────────────
            // 7. SQS Queues & DLQs for Email Notifications
            // ───────────────────────────────────────────────────────────
            var emailDlq = new Queue(this, "EmailDlq", new QueueProps
            {
                QueueName = "EmailDlq"
            });

            var emailQueue = new Queue(this, "EmailNotificationQueue", new QueueProps
            {
                QueueName = "EmailNotificationQueue",
                DeadLetterQueue = new DeadLetterQueue
                {
                    Queue            = emailDlq,
                    MaxReceiveCount  = 5
                }
            });


            // ───────────────────────────────────────────────────────────
            // 8. EventBridge Rule: Order Created → Email Queue
            // ───────────────────────────────────────────────────────────
            var orderCreatedRule = new Rule(this, "OrderCreatedRule", new RuleProps
            {
                EventBus     = bus,
                EventPattern = new EventPattern
                {
                    Source     = new[] { "market.orders" },
                    DetailType = new[] { "OrderCreated", "OrderFulfilled" }
                }
            });

            var emailSenderLambda = CreateLambda(
                "EmailSender",
                "EmailSender::EmailSender.Function::FunctionHandler"
            );
            emailSenderLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Actions   = new[] { "ses:SendEmail", "ses:SendRawEmail" },
                Resources = new[] { "*" }
            }));

            orderCreatedRule.AddTarget(new SqsQueue(emailQueue, new SqsQueueProps
            {
                Message = RuleTargetInput.FromObject(new Dictionary<string, object>
                {
                    { "detail-type", EventField.FromPath("$.detail-type") },
                    { "detail",      EventField.FromPath("$.detail")      }
                })
            }));

            emailQueue.GrantConsumeMessages(emailSenderLambda);
            emailSenderLambda.AddEventSource(new SqsEventSource(emailQueue));


            // ───────────────────────────────────────────────────────────
            // 9. SQS Queues & DLQs for Fulfillment
            // ───────────────────────────────────────────────────────────
            var fulfillDlq   = new Queue(this, "FullfilmentDlq", new QueueProps
            {
                QueueName = "FullfilmentDlq"
            });

            var fulfillQueue = new Queue(this, "FullfilmentQueue", new QueueProps
            {
                QueueName = "FullfilmentQueue",
                DeadLetterQueue = new DeadLetterQueue
                {
                    Queue           = fulfillDlq,
                    MaxReceiveCount = 5
                }
            });


            // ───────────────────────────────────────────────────────────
            // 10. EventBridge Rule: Payment Succeeded → Fulfillment & Email
            // ───────────────────────────────────────────────────────────
            var paymentSuccessRule = new Rule(this, "PaymentSucceededRule", new RuleProps
            {
                EventBus     = bus,
                EventPattern = new EventPattern
                {
                    Source     = new[] { "market.payment" },
                    DetailType = new[] { "PaymentSucceeded" }
                }
            });

            // to Fulfillment Queue
            paymentSuccessRule.AddTarget(new SqsQueue(fulfillQueue, new SqsQueueProps
            {
                Message = RuleTargetInput.FromEventPath("$.detail")
            }));

            // also to Email Queue
            paymentSuccessRule.AddTarget(new SqsQueue(emailQueue, new SqsQueueProps
            {
                Message = RuleTargetInput.FromObject(new Dictionary<string, object>
                {
                    { "detail-type", EventField.FromPath("$.detail-type") },
                    { "detail",      EventField.FromPath("$.detail")      }
                })
            }));

            fulfillQueue.GrantConsumeMessages(triggerFulfillmentLambda);
            triggerFulfillmentLambda.AddEventSource(new SqsEventSource(fulfillQueue));
        }
    }
}
