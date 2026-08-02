namespace CATRA.Core.Interfaces;

/// <summary>
/// Generic CRUD contract for integer-keyed domain repositories.
/// </summary>
/// <typeparam name="TEntity">Domain model type.</typeparam>
public interface IRepository<TEntity>
    where TEntity : class
{
    /// <summary>Returns every row, in no guaranteed order.</summary>
    IReadOnlyList<TEntity> GetAll();

    /// <summary>Returns the row with the given id, or <c>null</c> if absent.</summary>
    TEntity? GetById(int id);

    /// <summary>Inserts a new row and returns it with its generated id populated.</summary>
    TEntity Insert(TEntity entity);

    /// <summary>Updates an existing row. Returns <c>true</c> if a row was affected.</summary>
    bool Update(TEntity entity);

    /// <summary>Deletes the row with the given id. Returns <c>true</c> if a row was removed.</summary>
    bool Delete(int id);
}
