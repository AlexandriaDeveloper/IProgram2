

namespace Application.Helpers
{
    public class Error
    {
        public static readonly Error None = new(string.Empty, string.Empty);
        public static readonly Error NullValue = new("Error.NullValue", "The specified result value is null.");

        public Error(string code, string message)
        {
            Code = code;
            Message = message;
        }

        public string Code { get; }

        public string Message { get; }

        public static implicit operator string(Error error) => error.Code;
    }
    //TODO Change Move from Result to ProblemDetails
    // public class ProblemDetails
    // {
    //     public static readonly ProblemDetails None = new(string.Empty, null, Error.None, Array.Empty<Error>());
    //     public static readonly ProblemDetails NullValue = new("Error.NullValue", 404, Error.NullValue, Array.Empty<Error>());
    //     public string Title { get; init; }
    //     public int? Status { get; init; }
    //     public Error Error { get; init; }
    //     public Error[] Errors { get; init; }

    //     public ProblemDetails(string title, int? status, Error error, Error[] errors = null)
    //     {
    //         this.Title = title;
    //         this.Status = status;
    //         this.Error = error;
    //         this.Errors = errors;
    //     }

    //     public static implicit operator string(ProblemDetails error) => error.Title;
    // }
}