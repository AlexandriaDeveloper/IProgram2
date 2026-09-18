#nullable enable
using Application.Helpers;
using Core.Models;
using System.Threading.Tasks;

namespace Application.Interfaces
{
    public interface IDailyClosureGuard
    {
        Result ValidateDailyOpen(Daily? daily);
        Task<Result> ValidateFormDailyOpenAsync(Form? form);
        Task<Result> EnsureDailyOpenAsync(int dailyId);
        Task<Result> EnsureFormDailyOpenAsync(int formId);
        Task<Result> EnsureFormDailyOpenAsync(Form? form);
        Task<Result> EnsureFormDetailsDailyOpenAsync(int formDetailsId);
        Task<Result> EnsureDailyReferenceDailyOpenAsync(int dailyReferenceId);
        Task<Result> EnsureFormReferenceDailyOpenAsync(int formReferenceId);
    }
}
