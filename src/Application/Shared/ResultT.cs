using Application.Helpers;

namespace Application.Helpers
{
    public class Result<TValue> : Result
    {
        private readonly TValue _value;

        protected internal Result(TValue value, bool isSuccess, Error error)
            : base(isSuccess, error) =>
            _value = value;


        protected internal Result(TValue value, bool isSuccess, List<Error> errors)
            : base(isSuccess, errors) =>
            _value = value;

        // protected internal Result(TValue value, bool isSuccess, ProblemDetails error)
        //     : base(isSuccess, error) =>
        //     _value = value;

        public TValue Value => IsSuccess
            ? _value!
        : default(TValue)!;

        public static implicit operator Result<TValue>(TValue value) => Create(value);





        public static Result<TValue> Create<TValue>(TValue value) =>
                      value is not null ? Success(value) : Failure<TValue>(Error.NullValue);





    }
}