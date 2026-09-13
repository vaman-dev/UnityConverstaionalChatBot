#nullable enable
using System;

namespace Convai.Api.Transport
{
    public enum ConvaiApiErrorCategory
    {
        BadRequest,
        Authentication,
        Authorization,
        NotFound,
        Conflict,
        RateLimited,
        Server,
        Timeout,
        Transport,
        Serialization,
        Contract
    }

    public sealed class ConvaiApiException : Exception
    {
        public ConvaiApiException(
            string message,
            ConvaiApiErrorCategory category,
            int? statusCode = null,
            string? requestTraceId = null,
            TimeSpan? retryAfter = null,
            Exception? innerException = null,
            string? responseBody = null)
            : base(message, innerException)
        {
            Category = category;
            StatusCode = statusCode;
            RequestTraceId = requestTraceId;
            RetryAfter = retryAfter;
            ResponseBody = responseBody;
        }

        public ConvaiApiErrorCategory Category { get; }
        public int? StatusCode { get; }
        public string? RequestTraceId { get; }
        public TimeSpan? RetryAfter { get; }
        public string? ResponseBody { get; }
        public bool IsRetryable => Category == ConvaiApiErrorCategory.Timeout ||
                                   Category == ConvaiApiErrorCategory.Transport ||
                                   Category == ConvaiApiErrorCategory.RateLimited ||
                                   Category == ConvaiApiErrorCategory.Server;

        public static ConvaiApiException FromResponse(ConvaiApiResponse response)
        {
            ConvaiApiErrorCategory category = response.StatusCode switch
            {
                400 or 422 => ConvaiApiErrorCategory.BadRequest,
                401 => ConvaiApiErrorCategory.Authentication,
                403 => ConvaiApiErrorCategory.Authorization,
                404 => ConvaiApiErrorCategory.NotFound,
                409 => ConvaiApiErrorCategory.Conflict,
                429 => ConvaiApiErrorCategory.RateLimited,
                >= 500 => ConvaiApiErrorCategory.Server,
                _ => ConvaiApiErrorCategory.Contract
            };

            TimeSpan? retryAfter = null;
            if (int.TryParse(response.Header("Retry-After"), out int retrySeconds))
                retryAfter = TimeSpan.FromSeconds(retrySeconds);

            string? traceId = response.Header("x-request-trace-id") ?? response.Header("x-request-id");
            return new ConvaiApiException(
                $"Convai API returned HTTP {response.StatusCode}.",
                category,
                response.StatusCode,
                traceId,
                retryAfter,
                responseBody: response.BodyText);
        }
    }
}
