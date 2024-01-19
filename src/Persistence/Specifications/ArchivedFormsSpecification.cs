using Core.Models;
using Persistence.Helpers;

namespace Persistence.Specifications
{
    public class ArchivedFormsSpecification : Specification<Form>
    {
        public ArchivedFormsSpecification(FormArchivedParam param) : base(x => x.DailyId == null)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }

            AddOrderByDescending(x => x.Id);

            ApplyPaging(param.PageIndex, param.PageSize);
        }



    }

    public class ArchivedFormsCountSpecification : Specification<Form>
    {
        public ArchivedFormsCountSpecification(FormArchivedParam param) : base(x => x.DailyId == null)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }

            this.PaginationEnabled = false;
            // AddOrderByDescending(x => x.Id);

            // ApplyPaging(param.PageIndex, param.PageSize);
        }



    }
}
