using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Specifications
{
    public class SpecificationEvaluator<TEntity> where TEntity : Entity
    {

        public static IQueryable<TEntity> GetQuery(IQueryable<TEntity> inputQuery, ISpecification<TEntity> spec)
        {
            var query = inputQuery;

            if (spec.Criteria != null)
            {
                query = query.Where(spec.Criteria);
            }
            if (spec.Criterias.Count > 0)
            {
                foreach (var criteria in spec.Criterias)
                {
                    query = query.Where(criteria);
                }
            }
            if (spec.OrderBy != null)
            {
                query = query.OrderBy(spec.OrderBy);

            }
            if (spec.OrderByDescending != null)
            {
                query = query.OrderByDescending(spec.OrderByDescending);
            }

            if (spec.PaginationEnabled)
            {
                query = query.Skip(spec.Skip).Take(spec.Take);
            }

            query = spec.Includes.Aggregate(query, (current, include) => current.Include(include));
            return query;

        }

    }
}