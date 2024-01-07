using Application.Shared;

namespace Application.Helpers
{
    public class Result
    {
        protected internal Result(bool isSuccess, Error error)
        {
            if (isSuccess && error != Error.None)
            {
                throw new InvalidOperationException();
            }

            if (!isSuccess && error == Error.None)
            {
                throw new InvalidOperationException();
            }

            IsSuccess = isSuccess;
            Error = error;
        }



        // protected internal Result(bool isSuccess, ProblemDetails error)
        // {
        //     if (isSuccess && error != Error.None)
        //     {
        //         throw new InvalidOperationException();
        //     }

        //     if (!isSuccess && error == Error.None)
        //     {
        //         throw new InvalidOperationException();
        //     }

        //     IsSuccess = isSuccess;
        //     ProblemDetails = error;
        // }

        public bool IsSuccess { get; }

        public bool IsFailure => !IsSuccess;

        public Error Error { get; }
        public List<Error> Errors { get; }
        // public ProblemDetails ProblemDetails { get; }

        // public ProblemDetails ToProblemDetails() =>
        //     new(ProblemDetails.Title, ProblemDetails.Status, ProblemDetails.Error, ProblemDetails.Errors);


        // public static Result Success() => new(true, ProblemDetails.None);

        public static Result<TValue> Success<TValue>(TValue value) =>
            new(value, true, Error.None);

        public static Result Failure(Error error) =>
            new(false, error);

        // public static Result Failure(ProblemDetails error) =>
        // new(false, error);

        public static Result<TValue> Failure<TValue>(Error error) =>
            new(default, false, error);

        //     public static Result<TValue> Failure<TValue>(ProblemDetails error) =>
        //    new(default, false, error);

        // public static Result<TValue> Create<TValue>(TValue value) =>
        //     value is not null ? Success(value) : Failure<TValue>(ProblemDetails.NullValue);
        public static Result<TValue> Create<TValue>(TValue value) =>
     value is not null ? Success(value) : Failure<TValue>(Error.NullValue);



    }

}
