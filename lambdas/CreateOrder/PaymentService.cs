namespace CreateOrder;
public static class PaymentService
{
    public static async Task<(string paymentId, string paymentUrl)> CreatePaymentSessionAsync(string orderId, string provider)
    {
        // Simulate network delay
        await Task.Delay(200); // pretend we're calling Stripe or PayPal

        var paymentId = Guid.NewGuid().ToString();

        var url = provider switch
        {
            "Stripe" => $"https://checkout.stripe.com/pay/{paymentId}",
            "PayPal" => $"https://paypal.com/checkoutnow?token={paymentId}",
            _ => $"https://mockpay.io/session/{paymentId}"
        };

        return (paymentId, url);
    }
}
