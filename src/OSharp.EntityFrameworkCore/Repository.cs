﻿// -----------------------------------------------------------------------
//  <copyright file="Repository.cs" company="OSharp开源团队">
//      Copyright (c) 2014-2017 OSharp. All rights reserved.
//  </copyright>
//  <site>http://www.osharp.org</site>
//  <last-editor>郭明锋</last-editor>
//  <last-date>2017-11-15 19:20</last-date>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OSharp.Collections;
using OSharp.Data;
using OSharp.Dependency;
using OSharp.Exceptions;
using OSharp.Extensions;
using OSharp.Filter;
using OSharp.Mapping;
using OSharp.Secutiry;

using Z.EntityFramework.Plus;


namespace OSharp.Entity
{
    /// <summary>
    /// 实体数据存储操作类
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TKey">实体主键类型</typeparam>
    public class Repository<TEntity, TKey> : IRepository<TEntity, TKey>
        where TEntity : class, IEntity<TKey>
        where TKey : IEquatable<TKey>
    {
        private readonly DbContext _dbContext;
        private readonly DbSet<TEntity> _dbSet;
        private readonly ILogger _logger;

        /// <summary>
        /// 初始化一个<see cref="Repository{TEntity, TKey}"/>类型的新实例
        /// </summary>
        public Repository(IUnitOfWork unitOfWork)
        {
            UnitOfWork = unitOfWork;
            _dbContext = (DbContext)unitOfWork.GetDbContext<TEntity, TKey>();
            _dbSet = _dbContext.Set<TEntity>();
            _logger = ServiceLocator.Instance.GetLogger<Repository<TEntity, TKey>>();
        }

        /// <summary>
        /// 获取 当前单元操作对象
        /// </summary>
        public IUnitOfWork UnitOfWork { get; }

        /// <summary>
        /// 获取 <typeparamref name="TEntity"/>不跟踪数据更改（NoTracking）的查询数据源
        /// </summary>
        public virtual IQueryable<TEntity> Entities
        {
            get
            {
                Expression<Func<TEntity, bool>> dataFilterExp = GetDataFilter(DataAuthOperation.Read);
                return _dbSet.AsQueryable().AsNoTracking().Where(dataFilterExp);
            }
        }

        /// <summary>
        /// 获取 <typeparamref name="TEntity"/>跟踪数据更改（Tracking）的查询数据源
        /// </summary>
        public virtual IQueryable<TEntity> TrackEntities
        {
            get
            {
                Expression<Func<TEntity, bool>> dataFilterExp = GetDataFilter(DataAuthOperation.Read);
                return _dbSet.AsQueryable().Where(dataFilterExp);
            }
        }

        #region 同步方法

        /// <summary>
        /// 插入实体
        /// </summary>
        /// <param name="entities">实体对象集合</param>
        /// <returns>操作影响的行数</returns>
        public virtual int Insert(params TEntity[] entities)
        {
            Check.NotNull(entities, nameof(entities));
            for (int i = 0; i < entities.Length; i++)
            {
                TEntity entity = entities[i];
                entities[i] = entity.CheckICreatedTime<TEntity, TKey>();
            }
            _dbSet.AddRange(entities);
            return _dbContext.SaveChanges();
        }

        /// <summary>
        /// 以DTO为载体批量插入实体
        /// </summary>
        /// <typeparam name="TInputDto">添加DTO类型</typeparam>
        /// <param name="dtos">添加DTO信息集合</param>
        /// <param name="checkAction">添加信息合法性检查委托</param>
        /// <param name="updateFunc">由DTO到实体的转换委托</param>
        /// <returns>业务操作结果</returns>
        public virtual OperationResult Insert<TInputDto>(ICollection<TInputDto> dtos,
            Action<TInputDto> checkAction = null,
            Func<TInputDto, TEntity, TEntity> updateFunc = null) where TInputDto : IInputDto<TKey>
        {
            Check.NotNull(dtos, nameof(dtos));
            List<string> names = new List<string>();
            foreach (TInputDto dto in dtos)
            {
                try
                {
                    if (checkAction != null)
                    {
                        checkAction(dto);
                    }
                    TEntity entity = dto.MapTo<TEntity>();
                    if (updateFunc != null)
                    {
                        entity = updateFunc(dto, entity);
                    }
                    entity = entity.CheckICreatedTime<TEntity, TKey>();
                    _dbSet.Add(entity);
                }
                catch (OsharpException e)
                {
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, e.Message);
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                names.AddIfNotNull(GetNameValue(dto));
            }
            int count = _dbContext.SaveChanges();
            return count > 0
                ? new OperationResult(OperationResultType.Success,
                    names.Count > 0
                        ? "信息“{0}”添加成功".FormatWith(names.ExpandAndToString())
                        : "{0}个信息添加成功".FormatWith(dtos.Count))
                : new OperationResult(OperationResultType.NoChanged);
        }

        /// <summary>
        /// 删除实体
        /// </summary>
        /// <param name="entities">实体对象集合</param>
        /// <returns>操作影响的行数</returns>
        public virtual int Delete(params TEntity[] entities)
        {
            Check.NotNull(entities, nameof(entities));

            CheckDataAuth(DataAuthOperation.Delete, entities);
            _dbSet.RemoveRange(entities);
            return _dbContext.SaveChanges();
        }

        /// <summary>
        /// 删除指定编号的实体
        /// </summary>
        /// <param name="key">实体主键</param>
        /// <returns>操作影响的行数</returns>
        public virtual int Delete(TKey key)
        {
            CheckEntityKey(key, nameof(key));

            TEntity entity = _dbSet.Find(key);
            return Delete(entity);
        }

        /// <summary>
        /// 以标识集合批量删除实体
        /// </summary>
        /// <param name="ids">标识集合</param>
        /// <param name="checkAction">删除前置检查委托</param>
        /// <param name="deleteFunc">删除委托，用于删除关联信息</param>
        /// <returns>业务操作结果</returns>
        public virtual OperationResult Delete(ICollection<TKey> ids, Action<TEntity> checkAction = null, Func<TEntity, TEntity> deleteFunc = null)
        {
            Check.NotNull(ids, nameof(ids));
            List<string> names = new List<string>();
            foreach (TKey id in ids)
            {
                TEntity entity = _dbSet.Find(id);
                if (entity == null)
                {
                    continue;
                }
                try
                {
                    if (checkAction != null)
                    {
                        checkAction(entity);
                    }
                    if (deleteFunc != null)
                    {
                        entity = deleteFunc(entity);
                    }
                    CheckDataAuth(DataAuthOperation.Delete, entity);
                    _dbSet.Remove(entity);
                }
                catch (OsharpException e)
                {
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, e.Message);
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                names.AddIfNotNull(GetNameValue(entity));
            }
            int count = _dbContext.SaveChanges();
            return count > 0
                ? new OperationResult(OperationResultType.Success,
                    names.Count > 0
                        ? "信息“{0}”删除成功".FormatWith(names.ExpandAndToString())
                        : "{0}个信息删除成功".FormatWith(ids.Count))
                : new OperationResult(OperationResultType.NoChanged);
        }

        /// <summary>
        /// 删除所有符合特定条件的实体
        /// </summary>
        /// <param name="predicate">查询条件谓语表达式</param>
        /// <returns>操作影响的行数</returns>
        public virtual int DeleteBatch(Expression<Func<TEntity, bool>> predicate)
        {
            Check.NotNull(predicate, nameof(predicate));

            ((DbContextBase)_dbContext).BeginOrUseTransaction();
            return _dbSet.Where(predicate).Delete();
        }

        /// <summary>
        /// 更新实体对象
        /// </summary>
        /// <param name="entities">更新后的实体对象</param>
        /// <returns>操作影响的行数</returns>
        public virtual int Update(params TEntity[] entities)
        {
            Check.NotNull(entities, nameof(entities));

            CheckDataAuth(DataAuthOperation.Update, entities);
            _dbSet.UpdateRange(entities);
            return _dbContext.SaveChanges();
        }

        /// <summary>
        /// 以DTO为载体批量更新实体
        /// </summary>
        /// <typeparam name="TEditDto">更新DTO类型</typeparam>
        /// <param name="dtos">更新DTO信息集合</param>
        /// <param name="checkAction">更新信息合法性检查委托</param>
        /// <param name="updateFunc">由DTO到实体的转换委托</param>
        /// <returns>业务操作结果</returns>
        public virtual OperationResult Update<TEditDto>(ICollection<TEditDto> dtos,
            Action<TEditDto, TEntity> checkAction = null,
            Func<TEditDto, TEntity, TEntity> updateFunc = null) where TEditDto : IInputDto<TKey>
        {
            Check.NotNull(dtos, nameof(dtos));
            List<string> names = new List<string>();
            foreach (TEditDto dto in dtos)
            {
                try
                {
                    TEntity entity = _dbSet.Find(dto.Id);
                    if (entity == null)
                    {
                        return new OperationResult(OperationResultType.QueryNull);
                    }
                    if (checkAction != null)
                    {
                        checkAction(dto, entity);
                    }
                    entity = dto.MapTo(entity);
                    if (updateFunc != null)
                    {
                        entity = updateFunc(dto, entity);
                    }
                    CheckDataAuth(DataAuthOperation.Update, entity);
                    _dbSet.Update(entity);
                }
                catch (OsharpException e)
                {
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, e.Message);
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                names.AddIfNotNull(GetNameValue(dto));
            }
            int count = _dbContext.SaveChanges();
            return count > 0
                ? new OperationResult(OperationResultType.Success,
                    names.Count > 0
                        ? "信息“{0}”更新成功".FormatWith(names.ExpandAndToString())
                        : "{0}个信息更新成功".FormatWith(dtos.Count))
                : new OperationResult(OperationResultType.NoChanged);
        }

        /// <summary>
        /// 批量更新所有符合特定条件的实体
        /// </summary>
        /// <param name="predicate">查询条件的谓语表达式</param>
        /// <param name="updateExpression">属性更新表达式</param>
        /// <returns>操作影响的行数</returns>
        public virtual int UpdateBatch(Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TEntity>> updateExpression)
        {
            Check.NotNull(predicate, nameof(predicate));
            Check.NotNull(updateExpression, nameof(updateExpression));

            ((DbContextBase)_dbContext).BeginOrUseTransaction();
            return _dbSet.Where(predicate).Update(updateExpression);
        }

        /// <summary>
        /// 检查实体是否存在
        /// </summary>
        /// <param name="predicate">查询条件谓语表达式</param>
        /// <param name="id">编辑的实体标识</param>
        /// <returns>是否存在</returns>
        public virtual bool CheckExists(Expression<Func<TEntity, bool>> predicate, TKey id = default(TKey))
        {
            Check.NotNull(predicate, nameof(predicate));

            TKey defaultId = default(TKey);
            var entity = _dbSet.Where(predicate).Select(m => new { m.Id }).FirstOrDefault();
            bool exists = !typeof(TKey).IsValueType && ReferenceEquals(id, null) || id.Equals(defaultId)
                ? entity != null
                : entity != null && !entity.Id.Equals(id);
            return exists;
        }

        /// <summary>
        /// 查找指定主键的实体
        /// </summary>
        /// <param name="key">实体主键</param>
        /// <returns>符合主键的实体，不存在时返回null</returns>
        public virtual TEntity Get(TKey key)
        {
            CheckEntityKey(key, nameof(key));

            return _dbSet.Find(key);
        }

        /// <summary>
        /// 获取<typeparamref name="TEntity"/>不跟踪数据更改（NoTracking）的查询数据源，并可附加过滤条件及是否启用数据权限过滤
        /// </summary>
        /// <param name="predicate">数据过滤表达式</param>
        /// <param name="filterByDataAuth">是否使用数据权限过滤，数据权限一般用于存在用户实例的查询，系统查询不启用数据权限过滤</param>
        /// <returns>符合条件的数据集</returns>
        public virtual IQueryable<TEntity> Query(Expression<Func<TEntity, bool>> predicate = null, bool filterByDataAuth = true)
        {
            return TrackQuery(predicate, filterByDataAuth).AsNoTracking();
        }

        /// <summary>
        /// 获取<typeparamref name="TEntity"/>不跟踪数据更改（NoTracking）的查询数据源，并可Include导航属性
        /// </summary>
        /// <param name="includePropertySelectors">要Include操作的属性表达式</param>
        /// <returns>符合条件的数据集</returns>
        public virtual IQueryable<TEntity> Query(params Expression<Func<TEntity, object>>[] includePropertySelectors)
        {
            return TrackQuery(includePropertySelectors).AsNoTracking();
        }

        /// <summary>
        /// 获取<typeparamref name="TEntity"/>跟踪数据更改（Tracking）的查询数据源，并可附加过滤条件及是否启用数据权限过滤
        /// </summary>
        /// <param name="predicate">数据过滤表达式</param>
        /// <param name="filterByDataAuth">是否使用数据权限过滤，数据权限一般用于存在用户实例的查询，系统查询不启用数据权限过滤</param>
        /// <returns>符合条件的数据集</returns>
        public IQueryable<TEntity> TrackQuery(Expression<Func<TEntity, bool>> predicate = null, bool filterByDataAuth = true)
        {
            IQueryable<TEntity> query = _dbSet.AsQueryable();
            if (filterByDataAuth)
            {
                Expression<Func<TEntity, bool>> dataAuthExp = GetDataFilter(DataAuthOperation.Read);
                query = query.Where(dataAuthExp);
            }
            if (predicate == null)
            {
                return query;
            }
            return query.Where(predicate);
        }

        /// <summary>
        /// 获取<typeparamref name="TEntity"/>跟踪数据更改（Tracking）的查询数据源，并可Include导航属性
        /// </summary>
        /// <param name="includePropertySelectors">要Include操作的属性表达式</param>
        /// <returns>符合条件的数据集</returns>
        public virtual IQueryable<TEntity> TrackQuery(params Expression<Func<TEntity, object>>[] includePropertySelectors)
        {
            IQueryable<TEntity> query = _dbSet.AsQueryable();
            if (includePropertySelectors != null && includePropertySelectors.Length > 0)
            {
                foreach (Expression<Func<TEntity, object>> selector in includePropertySelectors)
                {
                    query = query.Include(selector);
                }
            }
            return query;
        }

        #endregion

        #region 异步方法

        /// <summary>
        /// 异步插入实体
        /// </summary>
        /// <param name="entities">实体对象集合</param>
        /// <returns>操作影响的行数</returns>
        public virtual async Task<int> InsertAsync(params TEntity[] entities)
        {
            Check.NotNull(entities, nameof(entities));

            for (int i = 0; i < entities.Length; i++)
            {
                TEntity entity = entities[i];
                entities[i] = entity.CheckICreatedTime<TEntity, TKey>();
            }

            await _dbSet.AddRangeAsync(entities);
            return await _dbContext.SaveChangesAsync();
        }

        /// <summary>
        /// 异步以DTO为载体批量插入实体
        /// </summary>
        /// <typeparam name="TInputDto">添加DTO类型</typeparam>
        /// <param name="dtos">添加DTO信息集合</param>
        /// <param name="checkAction">添加信息合法性检查委托</param>
        /// <param name="updateFunc">由DTO到实体的转换委托</param>
        /// <returns>业务操作结果</returns>
        public virtual async Task<OperationResult> InsertAsync<TInputDto>(ICollection<TInputDto> dtos,
            Func<TInputDto, Task> checkAction = null,
            Func<TInputDto, TEntity, Task<TEntity>> updateFunc = null) where TInputDto : IInputDto<TKey>
        {
            Check.NotNull(dtos, nameof(dtos));
            List<string> names = new List<string>();
            foreach (TInputDto dto in dtos)
            {
                try
                {
                    if (checkAction != null)
                    {
                        await checkAction(dto);
                    }
                    TEntity entity = dto.MapTo<TEntity>();
                    if (updateFunc != null)
                    {
                        entity = await updateFunc(dto, entity);
                    }
                    entity = entity.CheckICreatedTime<TEntity, TKey>();
                    await _dbSet.AddAsync(entity);
                }
                catch (OsharpException e)
                {
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, e.Message);
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                names.AddIfNotNull(GetNameValue(dto));
            }
            int count = await _dbContext.SaveChangesAsync();
            return count > 0
                ? new OperationResult(OperationResultType.Success,
                    names.Count > 0
                        ? "信息“{0}”添加成功".FormatWith(names.ExpandAndToString())
                        : "{0}个信息添加成功".FormatWith(dtos.Count))
                : OperationResult.NoChanged;
        }

        /// <summary>
        /// 异步删除实体
        /// </summary>
        /// <param name="entities">实体对象集合</param>
        /// <returns>操作影响的行数</returns>
        public virtual async Task<int> DeleteAsync(params TEntity[] entities)
        {
            Check.NotNull(entities, nameof(entities));

            CheckDataAuth(DataAuthOperation.Delete, entities);
            _dbSet.RemoveRange(entities);
            return await _dbContext.SaveChangesAsync();
        }

        /// <summary>
        /// 异步删除指定编号的实体
        /// </summary>
        /// <param name="key">实体编号</param>
        /// <returns>操作影响的行数</returns>
        public virtual async Task<int> DeleteAsync(TKey key)
        {
            CheckEntityKey(key, nameof(key));

            TEntity entity = await _dbSet.FindAsync(key);
            return await DeleteAsync(entity);
        }

        /// <summary>
        /// 异步以标识集合批量删除实体
        /// </summary>
        /// <param name="ids">标识集合</param>
        /// <param name="checkAction">删除前置检查委托</param>
        /// <param name="deleteFunc">删除委托，用于删除关联信息</param>
        /// <returns>业务操作结果</returns>
        public virtual async Task<OperationResult> DeleteAsync(ICollection<TKey> ids,
            Func<TEntity, Task> checkAction = null,
            Func<TEntity, Task<TEntity>> deleteFunc = null)
        {
            Check.NotNull(ids, nameof(ids));
            List<string> names = new List<string>();
            foreach (TKey id in ids)
            {
                TEntity entity = await _dbSet.FindAsync(id);
                if (entity == null)
                {
                    continue;
                }
                try
                {
                    if (checkAction != null)
                    {
                        await checkAction(entity);
                    }
                    if (deleteFunc != null)
                    {
                        entity = await deleteFunc(entity);
                    }
                    CheckDataAuth(DataAuthOperation.Delete, entity);
                    _dbSet.Remove(entity);
                }
                catch (OsharpException e)
                {
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, e.Message);
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                names.AddIfNotNull(GetNameValue(entity));
            }
            int count = await _dbContext.SaveChangesAsync();
            return count > 0
                ? new OperationResult(OperationResultType.Success,
                    names.Count > 0
                        ? "信息“{0}”删除成功".FormatWith(names.ExpandAndToString())
                        : "{0}个信息删除成功".FormatWith(ids.Count))
                : new OperationResult(OperationResultType.NoChanged);
        }

        /// <summary>
        /// 异步删除所有符合特定条件的实体
        /// </summary>
        /// <param name="predicate">查询条件谓语表达式</param>
        /// <returns>操作影响的行数</returns>
        public virtual async Task<int> DeleteBatchAsync(Expression<Func<TEntity, bool>> predicate)
        {
            Check.NotNull(predicate, nameof(predicate));

            await ((DbContextBase)_dbContext).BeginOrUseTransactionAsync();
            return await _dbSet.Where(predicate).DeleteAsync();
        }

        /// <summary>
        /// 异步更新实体对象
        /// </summary>
        /// <param name="entity">更新后的实体对象</param>
        /// <returns>操作影响的行数</returns>
        public virtual async Task<int> UpdateAsync(TEntity entity)
        {
            Check.NotNull(entity, nameof(entity));

            CheckDataAuth(DataAuthOperation.Update, entity);
            _dbSet.Update(entity);
            return await _dbContext.SaveChangesAsync();
        }

        /// <summary>
        /// 异步以DTO为载体批量更新实体
        /// </summary>
        /// <typeparam name="TEditDto">更新DTO类型</typeparam>
        /// <param name="dtos">更新DTO信息集合</param>
        /// <param name="checkAction">更新信息合法性检查委托</param>
        /// <param name="updateFunc">由DTO到实体的转换委托</param>
        /// <returns>业务操作结果</returns>
        public virtual async Task<OperationResult> UpdateAsync<TEditDto>(ICollection<TEditDto> dtos,
            Func<TEditDto, TEntity, Task> checkAction = null,
            Func<TEditDto, TEntity, Task<TEntity>> updateFunc = null) where TEditDto : IInputDto<TKey>
        {
            List<string> names = new List<string>();
            foreach (TEditDto dto in dtos)
            {
                try
                {
                    TEntity entity = await _dbSet.FindAsync(dto.Id);
                    if (entity == null)
                    {
                        return new OperationResult(OperationResultType.QueryNull);
                    }
                    if (checkAction != null)
                    {
                        await checkAction(dto, entity);
                    }
                    entity = dto.MapTo(entity);
                    if (updateFunc != null)
                    {
                        entity = await updateFunc(dto, entity);
                    }

                    CheckDataAuth(DataAuthOperation.Update, entity);
                    _dbSet.Update(entity);
                }
                catch (OsharpException e)
                {
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, e.Message);
                    return new OperationResult(OperationResultType.Error, e.Message);
                }
                names.AddIfNotNull(GetNameValue(dto));
            }
            int count = await _dbContext.SaveChangesAsync();
            return count > 0
                ? new OperationResult(OperationResultType.Success,
                    names.Count > 0
                        ? "信息“{0}”更新成功".FormatWith(names.ExpandAndToString())
                        : "{0}个信息更新成功".FormatWith(dtos.Count))
                : new OperationResult(OperationResultType.NoChanged);
        }

        /// <summary>
        /// 异步更新所有符合特定条件的实体
        /// </summary>
        /// <param name="predicate">查询条件谓语表达式</param>
        /// <param name="updateExpression">实体更新表达式</param>
        /// <returns>操作影响的行数</returns>
        public virtual async Task<int> UpdateBatchAsync(Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TEntity>> updateExpression)
        {
            Check.NotNull(predicate, nameof(predicate));
            Check.NotNull(updateExpression, nameof(updateExpression));

            await ((DbContextBase)_dbContext).BeginOrUseTransactionAsync();
            return await _dbSet.Where(predicate).UpdateAsync(updateExpression);
        }

        /// <summary>
        /// 异步检查实体是否存在
        /// </summary>
        /// <param name="predicate">查询条件谓语表达式</param>
        /// <param name="id">编辑的实体标识</param>
        /// <returns>是否存在</returns>
        public virtual async Task<bool> CheckExistsAsync(Expression<Func<TEntity, bool>> predicate, TKey id = default(TKey))
        {
            predicate.CheckNotNull(nameof(predicate));

            TKey defaultId = default(TKey);
            var entity = await _dbSet.Where(predicate).Select(m => new { m.Id }).FirstOrDefaultAsync();
            bool exists = !typeof(TKey).IsValueType && ReferenceEquals(id, null) || id.Equals(defaultId)
                ? entity != null
                : entity != null && !entity.Id.Equals(id);
            return exists;
        }

        /// <summary>
        /// 异步查找指定主键的实体
        /// </summary>
        /// <param name="key">实体主键</param>
        /// <returns>符合主键的实体，不存在时返回null</returns>
        public virtual async Task<TEntity> GetAsync(TKey key)
        {
            CheckEntityKey(key, nameof(key));

            return await _dbSet.FindAsync(key);
        }

        #endregion

        #region 私有方法

        private static void CheckEntityKey(object key, string keyName)
        {
            key.CheckNotNull("key");
            keyName.CheckNotNull("keyName");

            Type type = key.GetType();
            if (type == typeof(int))
            {
                Check.GreaterThan((int)key, keyName, 0);
            }
            else if (type == typeof(string))
            {
                Check.NotNullOrEmpty((string)key, keyName);
            }
            else if (type == typeof(Guid))
            {
                ((Guid)key).CheckNotEmpty(keyName);
            }
        }

        private static string GetNameValue(object value)
        {
            dynamic obj = value;
            try
            {
                return obj.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 检查指定操作的数据权限，验证要操作的数据是否符合特定的验证委托
        /// </summary>
        /// <param name="operation">数据权限操作</param>
        /// <param name="entities">要验证的实体对象</param>
        private static void CheckDataAuth(DataAuthOperation operation, params TEntity[] entities)
        {
            if (entities.Length == 0)
            {
                return;
            }
            Expression<Func<TEntity, bool>> exp = GetDataFilter(operation);
            Func<TEntity, bool> func = exp.Compile();
            bool flag = entities.All(func);
            if (!flag)
            {
                throw new OsharpException($"实体“{typeof(TEntity)}”的数据“{entities.ExpandAndToString(m => m.Id.ToString())}”进行“{operation.ToDescription()}”操作时权限不足");
            }
        }

        private static Expression<Func<TEntity, bool>> GetDataFilter(DataAuthOperation operation)
        {
            return FilterHelper.GetDataFilterExpression<TEntity>(operation: operation);
        }

        #endregion
    }
}