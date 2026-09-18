#nullable enable
using System.Threading.Tasks;
using Application.Helpers;
using Application.Interfaces;
using Core.Interfaces;
using Core.Models;

namespace Application.Services
{
    public class DailyClosureGuard : IDailyClosureGuard
    {
        public const string DailyClosedMessage = "لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.";
        public const string DailyNotFoundMessage = "اليومية غير موجودة";
        public const string FormNotFoundMessage = "عفوا الملف غير موجود";
        public const string FormDetailsNotFoundMessage = "عفوا التفاصيل غير موجودة";
        public const string ReferenceNotFoundMessage = "المرجع غير موجود.";

        private readonly IDailyRepository _dailyRepository;
        private readonly IFormRepository _formRepository;
        private readonly IFormDetailsRepository _formDetailsRepository;
        private readonly IDailyReferencesRepository _dailyReferencesRepository;
        private readonly IFormReferencesRepository _formReferencesRepository;

        public DailyClosureGuard(
            IDailyRepository dailyRepository,
            IFormRepository formRepository,
            IFormDetailsRepository formDetailsRepository,
            IDailyReferencesRepository dailyReferencesRepository,
            IFormReferencesRepository formReferencesRepository)
        {
            _dailyRepository = dailyRepository;
            _formRepository = formRepository;
            _formDetailsRepository = formDetailsRepository;
            _dailyReferencesRepository = dailyReferencesRepository;
            _formReferencesRepository = formReferencesRepository;
        }

        public Result ValidateDailyOpen(Daily? daily)
        {
            if (daily == null)
            {
                return Result.Failure(new Error("404", DailyNotFoundMessage));
            }

            if (daily.Closed)
            {
                return Result.Failure(new Error("400", DailyClosedMessage));
            }

            return Result.Success();
        }

        public async Task<Result> ValidateFormDailyOpenAsync(Form? form)
        {
            if (form == null)
            {
                return Result.Failure(new Error("404", FormNotFoundMessage));
            }

            if (form.DailyId.HasValue)
            {
                return await EnsureDailyOpenAsync(form.DailyId.Value);
            }

            return Result.Success();
        }

        public async Task<Result> EnsureDailyOpenAsync(int dailyId)
        {
            var daily = await _dailyRepository.GetById(dailyId);
            return ValidateDailyOpen(daily);
        }

        public async Task<Result> EnsureFormDailyOpenAsync(int formId)
        {
            var form = await _formRepository.GetById(formId);
            return await ValidateFormDailyOpenAsync(form);
        }

        public Task<Result> EnsureFormDailyOpenAsync(Form? form)
        {
            return ValidateFormDailyOpenAsync(form);
        }

        public async Task<Result> EnsureFormDetailsDailyOpenAsync(int formDetailsId)
        {
            var detail = await _formDetailsRepository.GetById(formDetailsId);
            if (detail == null)
            {
                return Result.Failure(new Error("404", FormDetailsNotFoundMessage));
            }

            return await EnsureFormDailyOpenAsync(detail.FormId);
        }

        public async Task<Result> EnsureDailyReferenceDailyOpenAsync(int dailyReferenceId)
        {
            var reference = await _dailyReferencesRepository.GetById(dailyReferenceId);
            if (reference == null)
            {
                return Result.Failure(new Error("404", ReferenceNotFoundMessage));
            }

            return await EnsureDailyOpenAsync(reference.DailyId);
        }

        public async Task<Result> EnsureFormReferenceDailyOpenAsync(int formReferenceId)
        {
            var reference = await _formReferencesRepository.GetById(formReferenceId);
            if (reference == null)
            {
                return Result.Failure(new Error("404", ReferenceNotFoundMessage));
            }

            return await EnsureFormDailyOpenAsync(reference.FormId);
        }
    }
}
