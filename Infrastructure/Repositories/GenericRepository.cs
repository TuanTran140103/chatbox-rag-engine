using System.Linq;
using System.Linq.Expressions;
// using MarkdownGenQAs.Application;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarkdownGenQAs.Infrastructure.Repositories;

public class GenericRepository<T> : IGenericRepository<T> where T : BaseEntity
{
    protected readonly ApplicationContext _context;
    protected readonly DbSet<T> _dbSet;

    public GenericRepository(ApplicationContext context)
    {
        _context = context;
        _dbSet = context.Set<T>();
    }

    public IQueryable<T> Query => _dbSet.AsQueryable();

    public virtual async Task<T?> GetByIdAsync(Guid id)
    {
        return await _dbSet.FindAsync(id);
    }

    public virtual async Task<IEnumerable<T>> GetAllAsync()
    {
        return await _dbSet.ToListAsync();
    }

    public virtual async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, Func<IQueryable<T>, IQueryable<T>>? include = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AsQueryable();
        if (include != null) query = include(query);
        return await query.Where(predicate).ToListAsync(cancellationToken);
    }

    public virtual async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, Func<IQueryable<T>, IQueryable<T>>? include = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AsQueryable();
        if (include != null) query = include(query);
        return await query.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public virtual async Task AddAsync(T entity)
    {
        await _dbSet.AddAsync(entity);
    }

    public virtual void Update(T entity)
    {
        _dbSet.Update(entity);
    }

    public virtual void Delete(T entity)
    {
        _dbSet.Remove(entity);
    }
}
