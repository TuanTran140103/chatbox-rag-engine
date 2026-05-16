using System.Linq;
using System.Linq.Expressions;
using MarkdownGenQAs.Models.Entities;

namespace MarkdownGenQAs.Application.Interfaces.Repository;

public interface IGenericRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, Func<IQueryable<T>, IQueryable<T>>? include = null, CancellationToken cancellationToken = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, Func<IQueryable<T>, IQueryable<T>>? include = null, CancellationToken cancellationToken = default);
    IQueryable<T> Query { get; }
    Task AddAsync(T entity);
    void Update(T entity);
    void Delete(T entity);
}
