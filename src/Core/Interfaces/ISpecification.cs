using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface ISpecification<TEntity>
    {
        // int PageIndex { get; }
        // int PageSize { get; }
        int Skip { get; }
        int Take { get; }
        bool PaginationEnabled { get; }
        List<string> IncludeStrings { get; }
        Expression<Func<TEntity, bool>> Criteria { get; }
        List<Expression<Func<TEntity, bool>>> Criterias { get; }
        List<Expression<Func<TEntity, object>>> Includes { get; }

        Expression<Func<TEntity, object>> OrderBy { get; }
        Expression<Func<TEntity, object>> OrderByDescending { get; }


    }
}