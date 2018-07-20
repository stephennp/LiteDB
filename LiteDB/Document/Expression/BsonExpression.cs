﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteDB
{
    /// <summary>
    /// Compile and execute string expressions using BsonDocuments. Used in all document manipulation (transform, filter, indexes, updates). See https://github.com/mbdavid/LiteDB/wiki/Expressions
    /// </summary>
    public class BsonExpression
    {
        /// <summary>
        /// Get formatted expression
        /// </summary>
        public string Source { get; internal set; }

        /// <summary>
        /// Indicate expression type
        /// </summary>
        public BsonExpressionType Type { get; internal set; }

        /// <summary>
        /// If true, this expression do not change if same document/paramter are passed (only few methods change - like NOW() - or parameters)
        /// </summary>
        internal bool IsImmutable { get; set; }

        /// <summary>
        /// If true, indicate that it's possible execute this expression without document - always same result
        /// </summary>
        internal bool IsConstant { get; set; }

        /// <summary>
        /// Get/Set parameter values that will be used on expression execution
        /// </summary>
        public BsonDocument Parameters { get; private set; } = new BsonDocument();

        /// <summary>
        /// In conditional/or/and expressions, indicate Left side
        /// </summary>
        internal BsonExpression Left { get; set; }

        /// <summary>
        /// In conditional/or/and expressions, indicate Rigth side
        /// </summary>
        internal BsonExpression Right { get; set; }

        /// <summary>
        /// Get transformed LINQ expression
        /// </summary>
        internal Expression Expression { get; set; }

        /// <summary>
        /// Fill this hashset with all fields used in root level of document (be used to partial deserialize) - "$" means all fields
        /// </summary>
        public HashSet<string> Fields { get; set; }

        /// <summary>
        /// Indicate that expression are binary conditional expression (=, >, ...)
        /// </summary>
        internal bool IsConditional =>
            this.Type == BsonExpressionType.Equal ||
            this.Type == BsonExpressionType.Like ||
            this.Type == BsonExpressionType.Between ||
            this.Type == BsonExpressionType.GreaterThan ||
            this.Type == BsonExpressionType.GreaterThanOrEqual ||
            this.Type == BsonExpressionType.LessThan ||
            this.Type == BsonExpressionType.LessThanOrEqual ||
            this.Type == BsonExpressionType.NotEqual ||
            this.Type == BsonExpressionType.In;

        /// <summary>
        /// Compiled Expression into a function to be executed
        /// </summary>
        private Func<IEnumerable<BsonDocument>, IEnumerable<BsonValue>, BsonDocument, IEnumerable<BsonValue>> _func;

        /// <summary>
        /// Only internal ctor (from BsonParserExpression)
        /// </summary>
        internal BsonExpression()
        {
        }

        /// <summary>
        /// Implicit string converter
        /// </summary>
        public static implicit operator String(BsonExpression expr)
        {
            return expr.Source;
        }

        /// <summary>
        /// Implicit string converter
        /// </summary>
        public static implicit operator BsonExpression(String expr)
        {
            return BsonExpression.Create(expr);
        }

        #region Compile and execute

        /// <summary>
        /// Execute expression with an empty document (used only for resolve math/functions).
        /// </summary>
        public IEnumerable<BsonValue> Execute(bool includeNullIfEmpty = true)
        {
            var docs = new BsonDocument[] { new BsonDocument() };

            return this.Execute(docs, docs, includeNullIfEmpty);
        }

        /// <summary>
        /// Execute expression and returns IEnumerable values (can returns NULL if no elements).
        /// </summary>
        public IEnumerable<BsonValue> Execute(BsonDocument doc, bool includeNullIfEmpty = true)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var docs = new BsonDocument[] { doc };

            return this.Execute(docs, docs, includeNullIfEmpty);
        }

        /// <summary>
        /// Execute expression and returns IEnumerable values (can returns NULL if no elements).
        /// </summary>
        public IEnumerable<BsonValue> Execute(IEnumerable<BsonDocument> doc, bool includeNullIfEmpty = true)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            return this.Execute(doc, doc, includeNullIfEmpty);
        }

        /// <summary>
        /// Execute expression and returns IEnumerable values (can returns NULL if no elements).
        /// </summary>
        internal IEnumerable<BsonValue> Execute(IEnumerable<BsonDocument> root, IEnumerable<BsonValue> current, bool includeNullIfEmpty = true)
        {
            if (this.Type == BsonExpressionType.Empty)
            {
                foreach(var doc in root)
                {
                    yield return doc;
                }
            }
            else
            {
                var index = 0;
                var values = _func(root, current ?? root, this.Parameters);

                foreach (var value in values)
                {
                    index++;
                    yield return value;
                }

                if (index == 0 && includeNullIfEmpty) yield return BsonValue.Null;
            }
        }

        #endregion

        #region Static method

        private static ConcurrentDictionary<string, BsonExpression> _cache = new ConcurrentDictionary<string, BsonExpression>();

        /// <summary>
        /// Create an empty expression - Return same doc (similar to "$")
        /// </summary>
        public static BsonExpression Empty => new BsonExpression { Type = BsonExpressionType.Empty };

        /// <summary>
        /// Parse string and create new instance of BsonExpression - can be cached
        /// </summary>
        public static BsonExpression Create(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentNullException(nameof(expression));

            if (!_cache.TryGetValue(expression, out var expr))
            {
                expr = Parse(new Tokenizer(expression), true);

                // if passed string expression are different from formatted expression, try add in cache "unformatted" expression too
                if (expression != expr.Source)
                {
                    _cache.TryAdd(expression, expr);
                }
            }

            // return a copy from cache WITHOUT parameters
            return new BsonExpression
            {
                Expression = expr.Expression,
                IsConstant = expr.IsConstant,
                IsImmutable = expr.IsImmutable,
                Fields = expr.Fields,
                Left = expr.Left,
                Right = expr.Right,
                Source = expr.Source,
                Type = expr.Type,
                _func = expr._func
            };
        }

        /// <summary>
        /// Parse string and create new instance of BsonExpression - can be cached
        /// </summary>
        public static BsonExpression Create(string expression, params BsonValue[] args)
        {
            var expr = Create(expression);

            for(var i = 0; i < args.Length; i++)
            {
                expr.Parameters[i.ToString()] = args[i];
            }

            return expr;
        }

        /// <summary>
        /// Parse string and create new instance of BsonExpression - can be cached
        /// </summary>
        public static BsonExpression Create(string expression, BsonDocument parameters)
        {
            var expr = Create(expression);

            expr.Parameters = parameters;

            return expr;
        }

        /// <summary>
        /// Parse tokenizer and create new instance of BsonExpression - for now, do not use cache
        /// </summary>
        internal static BsonExpression Create(Tokenizer tokenizer, BsonDocument parameters)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));

            var expr = Parse(tokenizer, true);

            // return a copy from cache using new Parameters
            return new BsonExpression
            {
                Expression = expr.Expression,
                IsConstant = expr.IsConstant,
                IsImmutable = expr.IsImmutable,
                Parameters = parameters ?? new BsonDocument(),
                Fields = expr.Fields,
                Left = expr.Left,
                Right = expr.Right,
                Source = expr.Source,
                Type = expr.Type,
                _func = expr._func
            };
        }

        /// <summary>
        /// Parse and compile string expression and return BsonExpression
        /// </summary>
        internal static BsonExpression Parse(Tokenizer tokenizer, bool isRoot)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));

            var root = Expression.Parameter(typeof(IEnumerable<BsonDocument>), "root");
            var current = Expression.Parameter(typeof(IEnumerable<BsonValue>), "current");
            var parameters = Expression.Parameter(typeof(BsonDocument), "parameters");

            var expr = BsonExpressionParser.ParseFullExpression(tokenizer, root, current, parameters, isRoot);

            // before compile try find in cache if this source already has in cache (already compiled)
            var cached = _cache.GetOrAdd(expr.Source, (s) =>
            {
                // compile linq expression (with left+right expressions)
                Compile(expr, root, current, parameters);
                return expr;
            });

            return cached;
        }

        private static void Compile(BsonExpression expr, ParameterExpression root, ParameterExpression current, ParameterExpression parameters)
        {
            var lambda = System.Linq.Expressions.Expression.Lambda<Func<IEnumerable<BsonDocument>, IEnumerable<BsonValue>, BsonDocument, IEnumerable<BsonValue>>>(expr.Expression, root, current, parameters);

            expr._func = lambda.Compile();

            // compile child expressions (left/right)
            if (expr.Left != null) Compile(expr.Left, root, current, parameters);
            if (expr.Right != null) Compile(expr.Right, root, current, parameters);
        }

        #endregion

        #region MethodCall quick access

        /// <summary>
        /// Load all static methods from BsonExpressionMethods class. Use a dictionary using name + parameter count
        /// </summary>
        private static Dictionary<string, MethodInfo> _methods =
            typeof(BsonExpressionMethods).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(m => m.Name.ToUpper() + "~" + m.GetParameters().Length);

        /// <summary>
        /// Get expression method with same name and same parameter - return null if not found
        /// </summary>
        internal static MethodInfo GetMethod(string name, int parameterCount)
        {
            var key = name.ToUpper() + "~" + parameterCount;

            return _methods.GetOrDefault(key);
        }

        private static void RegisterMethod(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (method.IsStatic == false || method.IsPublic == false) throw new InvalidOperationException("Custom BsonExpression methods must be static and public.");

            _methods[method.Name.ToUpper() + "~" + method.GetParameters().Length] = method;
        }

        /// <summary>
        /// Register a new method to work with BsonExpressions. Method must be public/static method
        /// </summary>
        public static void RegisterMethod(Func<IEnumerable<BsonValue>> method)
        {
            RegisterMethod(method.Method);
        }

        /// <summary>
        /// Register a new method to work with BsonExpressions. Method must be public/static method
        /// </summary>
        public static void RegisterMethod(Func<IEnumerable<BsonValue>, IEnumerable<BsonValue>> method)
        {
            RegisterMethod(method.Method);
        }

        /// <summary>
        /// Register a new method to work with BsonExpressions. Method must be public/static method
        /// </summary>
        public static void RegisterMethod(Func<IEnumerable<BsonValue>, IEnumerable<BsonValue>, IEnumerable<BsonValue>> method)
        {
            RegisterMethod(method.Method);
        }

        /// <summary>
        /// Register a new method to work with BsonExpressions. Method must be public/static method
        /// </summary>
        public static void RegisterMethod(Func<IEnumerable<BsonValue>, IEnumerable<BsonValue>, IEnumerable<BsonValue>, IEnumerable<BsonValue>> method)
        {
            RegisterMethod(method.Method);
        }

        #endregion

        public override string ToString()
        {
            return $"{this.Source} ({this.Type})";
        }
    }
}