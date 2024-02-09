
using Microsoft.AspNetCore.Mvc.ModelBinding;

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
        protected internal Result(bool isSuccess, List<Error> errors)
        {
            if (isSuccess && errors.Count > 0)
            {
                throw new InvalidOperationException();
            }

            if (!isSuccess && errors.Count == 0)
            {
                throw new InvalidOperationException();
            }

            IsSuccess = isSuccess;
            Errors = errors;
            Error = errors.Count > 0 ? new Error("1000", "ValidationError") : Error.None;
        }




        public bool IsSuccess { get; }

        public bool IsFailure => !IsSuccess;

        public Error Error { get; }
        public List<Error> Errors { get; }


        public static Result ValidationErrors(IEnumerable<ModelError> errors) =>
            new(false, errors.Select(x => new Error("1000", x.ErrorMessage)).ToList());

        public static Result Success() =>
            new(true, Error.None);

        public static Result Failure(Error error) =>
            new(false, error);


        public static Result<TValue> Success<TValue>(TValue value) =>
                 new(value, true, Error.None);



        public static Result<TValue> Failure<TValue>(Error error) =>
            new(default, false, error);

        public static Result<TValue> ValidationErrors<TValue>(IEnumerable<ModelError> errors) =>
            new(default, false, errors.Select(x => new Error("1000", x.ErrorMessage)).ToList());



    }

}
