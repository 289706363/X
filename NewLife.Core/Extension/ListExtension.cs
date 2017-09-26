﻿using System.Collections.Generic;
using System.Linq;

namespace System
{
    /// <summary>扩展List，支持遍历中修改元素</summary>
    public static class ListExtension
    {
        /// <summary>搜索并返回第一个，支持遍历中修改元素</summary>
        /// <param name="list">实体列表</param>
        /// <param name="match">条件</param>
        /// <returns></returns>
        public static T Find<T>(this IList<T> list, Predicate<T> match)
        {
            if (list is List<T> list2) return list2.Find(match);

            return list.FirstOrDefault(e => match(e));
        }

        /// <summary>搜索并返回第一个，支持遍历中修改元素</summary>
        /// <param name="list">实体列表</param>
        /// <param name="match">条件</param>
        /// <returns></returns>
        public static IList<T> FindAll<T>(this IList<T> list, Predicate<T> match)
        {
            if (list is List<T> list2) return list2.FindAll(match);

            return list.Where(e => match(e)).ToList();
        }
    }
}