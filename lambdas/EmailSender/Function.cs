using System.Text.Json.Serialization;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace EmailSender;

using Amazon.Lambda.SQSEvents;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using System.Text.Json;

public class Function
{
    private static readonly IAmazonSimpleEmailService sesClient = new AmazonSimpleEmailServiceClient();

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var message in sqsEvent.Records)
        {
            //context.Logger.LogInformation(message.Body);
            // Parse the message body (assume it's JSON, e.g. with email and order info)
            var payload = JsonSerializer.Deserialize<EventWrapper>(message.Body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            context.Logger.LogInformation("Message parsed");

            context.Logger.LogInformation(payload.Detail.GetRawText());

            switch (payload.DetailType)
            {
                case "OrderCreated":
                    var order = payload.Detail.Deserialize<OrderCreatedEvent>();
                    var emailBody = BuildOrderSummaryEmail(order);
                    await SendEmail(order.CustomerEmail, emailBody);
                    break;
                // case "PaymentSucceeded":
                    // var payment = JsonSerializer.Deserialize<PaymentSucceededDetail>(payload.Detail);
                    // var emailBody2 = RenderPaymentSuccessEmail(payment);
                    // await SendEmail(payment.Email, "Payment Successful", emailBody2);
                    // break;
            }
            context.Logger.LogInformation("Message sent");
        }
    }

    private async Task SendEmail(string destination, Message body)
    {
        var sendRequest = new SendEmailRequest
        {
            Source = "anderhil@gmail.com", // Must be a verified SES identity
            Destination = new Destination { ToAddresses = new List<string> { destination } },
            Message = body
        };

        await sesClient.SendEmailAsync(sendRequest);
    } 
    private static Message BuildOrderSummaryEmail(OrderCreatedEvent order)
    {
        var payUrl = $"https://your-domain.com/pay/{order.OrderId}"; // Replace with your real domain

        var itemListHtml = string.Join("", order.ItemIds.Select(id => $"<li>{id}</li>"));

        var html = $@"
        <html>
            <body>
                <h2>Thank you for your order!</h2>
                <p>Order ID: <strong>{order.OrderId}</strong></p>
                <p>Items:</p>
                <ul>
                    {itemListHtml}
                </ul>
                <p>Total: <strong>{order.TotalAmount} {order.Currency}</strong></p>
                <p>Payment Type: <strong>{order.PaymentType}</strong></p>
                <p>
                    <a href='{payUrl}'>Click here to pay for your order</a>
                </p>
                <p>If you have any questions, reply to this email.</p>
            </body>
        </html>";

        return new Message
        {
            Subject = new Content("Your Order Confirmation"),
            Body = new Body
            {
                Html = new Content(html)
            }
        };
        
    }
}

public class EventWrapper
{
    [JsonPropertyName("detail-type")]
    public string DetailType { get; set; }

    [JsonPropertyName("detail")]
    public JsonElement Detail { get; set; }
}

// Example event model
public class OrderCreatedEvent
{
    public string OrderId { get; set; } = string.Empty; 
    public List<string> ItemIds { get; set; } = new();
    public string Currency { get; set; } = "EUR";
    public int TotalAmount { get; set; }
    public string CustomerEmail { get; set; } = string.Empty;
    public string PaymentType { get; set; } = string.Empty;
}