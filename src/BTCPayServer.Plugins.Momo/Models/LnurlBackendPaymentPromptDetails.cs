 // Models/LnurlBackendPaymentPromptDetails.cs
   using System;
   using NBitcoin;
   using Newtonsoft.Json;

   namespace BTCPayServer.Plugins.Momo.Models;

   /// <summary>
   /// Serialized into PaymentPrompt.Details by ConfigurePrompt.
   /// Read back by LnurlVerifyListener during polling.
   /// </summary>
   public class LnurlBackendPaymentPromptDetails
   {
       [JsonProperty("bolt11")]
       public string Bolt11 { get; set; }

       /// <summary>Hex-encoded payment hash. Used as PaymentData.Id for idempotency.</summary>
       [JsonProperty("paymentHash")]
       public string PaymentHash { get; set; }

       /// <summary>uint256 form of payment_hash. Used for SHA256 preimage validation.</summary>
       [JsonProperty("paymentHashUint")]
       public uint256 PaymentHashUint { get; set; }

       [JsonProperty("verifyUrl")]
       public string VerifyUrl { get; set; }

       /// <summary>BOLT11 expiry. Expired transition only when this passes.</summary>
       [JsonProperty("expiresAt")]
       public DateTimeOffset ExpiresAt { get; set; }

       /// <summary>Set by listener when settled. null until then.</summary>
       [JsonProperty("preimage")]
       public string Preimage { get; set; }
   }
