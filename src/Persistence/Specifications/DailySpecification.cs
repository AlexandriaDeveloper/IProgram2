
using Core.Models;
using Persistence.Helpers;

namespace Persistence.Specifications
{
    public class DailySpecification : Specification<Daily>
    {
        public DailySpecification(DailyParam param)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (param.StartDate.HasValue && param.EndDate.HasValue)
            {

                AddCriteries(x => x.DailyDate <= param.EndDate.Value && x.DailyDate >= param.StartDate.Value);
            }
            AddOrderByDescending(x => x.Id);

            ApplyPaging(param.PageIndex, param.PageSize);
        }
    }

    public class DailyCountSpecification : Specification<Daily>
    {
        public DailyCountSpecification(DailyParam param)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (param.StartDate.HasValue && param.EndDate.HasValue)
            {
                AddCriteries(x => x.DailyDate <= param.EndDate.Value && x.DailyDate >= param.StartDate.Value);
            }
        }
    }
}