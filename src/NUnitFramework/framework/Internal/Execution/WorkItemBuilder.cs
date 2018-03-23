// ***********************************************************************
// Copyright (c) 2017 Charlie Poole, Rob Prouse
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework.Interfaces;

namespace NUnit.Framework.Internal.Execution
{
    public class SetupOrTearDown : TestMethod
    {
        public enum MethodType
        {
            SetUp,
            TearDown
        }

        private string _testType;

        public SetupOrTearDown(IMethodInfo method, MethodType type) : this(method, null, type)
        {
        }

        public SetupOrTearDown(IMethodInfo method, Test testCase, MethodType type) : base(method, testCase)
        {
            this.Parent = testCase;
            this._testType = type.ToString();
        }

        public override string TestType => _testType;
    }

    public class MyWorker : CompositeWorkItem
    {
        public MyWorker(TestSuite suite, ITestFilter childFilter) : base(suite, childFilter) { }

        protected override void PerformWork()
        {
            Console.WriteLine();
            base.PerformWork();
        }
    }

    /// <summary>
    /// WorkItemBuilder class knows how to build a tree of work items from a tree of tests
    /// </summary>
    public static class WorkItemBuilder
    {
        #region Static Factory Method

        /// <summary>
        /// Creates a work item.
        /// </summary>
        /// <param name="test">The test for which this WorkItem is being created.</param>
        /// <param name="filter">The filter to be used in selecting any child Tests.</param>
        /// <param name="recursive">True if child work items should be created and added.</param>
        /// <returns></returns>
        static public WorkItem CreateWorkItem(ITest test, ITestFilter filter, bool recursive = false)
        {
            TestSuite suite = test as TestSuite;
            if (suite == null)
            {
                TestFixture parentFixture = test.Parent as TestFixture ?? test.Parent?.Parent as TestFixture;
                if (parentFixture == null)
                {
                    return new SimpleWorkItem((TestMethod)test, filter);
                }

                // In normal operation we should always get the methods from the parent fixture.
                // However, some of NUnit's own tests can create a TestMethod without a parent 
                // fixture. Most likely, we should stop doing this, but it affects 100s of cases.

                var setUpMethods = parentFixture.SetUpMethods;
                var tearDownMethods = parentFixture.TearDownMethods;

                if (setUpMethods.Any() || tearDownMethods.Any())
                {
                    var testSuite = new TestSuite(test.Parent.FullName, test.Name)
                    {
                        Parent = test.Parent,
                    };
                    var attr = new NonParallelizableAttribute();
                    attr.ApplyToTest(testSuite);

                    foreach (var setupMethod in setUpMethods)
                    {
                        var setup = new SetupOrTearDown(new MethodWrapper(typeof(int), setupMethod), testSuite, SetupOrTearDown.MethodType.SetUp);
                        testSuite.Tests.Add(setup);
                    }

                    ((TestMethod)test).Parent = testSuite;
                    testSuite.Tests.Add(test);
                    foreach (var tearDownMethod in tearDownMethods)
                    {
                        var tearDown = new SetupOrTearDown(new MethodWrapper(typeof(int), tearDownMethod), testSuite, SetupOrTearDown.MethodType.TearDown);
                        testSuite.Tests.Add(tearDown);
                    }

                    testSuite.Tests.ToList().ForEach(t => new NonParallelizableAttribute().ApplyToTest((Test)t));
                    // public MethodInfo[] SetUpMethods { get; protected set; }
                    //var prop = parentFixture.GetType().GetProperty(nameof(parentFixture.SetUpMethods), BindingFlags.Instance | BindingFlags.Public);
                    //prop.SetValue(parentFixture, new MethodInfo[0], null);

                    //var prop1 = parentFixture.GetType().GetProperty(nameof(parentFixture.TearDownMethods), BindingFlags.Instance | BindingFlags.Public);
                    //prop1.SetValue(parentFixture, new MethodInfo[0], null);
                    suite = testSuite;
                }
                else
                {
                    return new SimpleWorkItem((TestMethod)test, filter);
                }
            }

            var work = new CompositeWorkItem(suite, filter);

            if (recursive)
            {
                int countOrderedItems = 0;

                foreach (var childTest in suite.Tests)
                {
                    if (filter.Pass(childTest))
                    {
                        var childItem = CreateWorkItem(childTest, filter, recursive);

#if APARTMENT_STATE
                        if (childItem.TargetApartment == ApartmentState.Unknown && work.TargetApartment != ApartmentState.Unknown)
                            childItem.TargetApartment = work.TargetApartment;
#endif

                        if (childTest.Properties.ContainsKey(PropertyNames.Order))
                        {
                            work.Children.Insert(0, childItem);
                            countOrderedItems++;
                        }
                        else
                            work.Children.Add(childItem);
                    }
                }

                if (countOrderedItems > 0)
                    work.Children.Sort(0, countOrderedItems, new WorkItemOrderComparer());
            }

            return work;
        }

        #endregion

        private class WorkItemOrderComparer : IComparer<WorkItem>
        {
            /// <summary>
            /// Compares two objects and returns a value indicating whether one is less than, equal to, or greater than the other.
            /// </summary>
            /// <returns>
            /// A signed integer that indicates the relative values of <paramref name="x"/> and <paramref name="y"/>, as shown in the following table.Value Meaning Less than zero<paramref name="x"/> is less than <paramref name="y"/>.Zero<paramref name="x"/> equals <paramref name="y"/>.Greater than zero<paramref name="x"/> is greater than <paramref name="y"/>.
            /// </returns>
            /// <param name="x">The first object to compare.</param><param name="y">The second object to compare.</param>
            public int Compare(WorkItem x, WorkItem y)
            {
                var xKey = int.MaxValue;
                var yKey = int.MaxValue;

                if (x.Test.Properties.ContainsKey(PropertyNames.Order))
                    xKey = (int)x.Test.Properties[PropertyNames.Order][0];

                if (y.Test.Properties.ContainsKey(PropertyNames.Order))
                    yKey = (int)y.Test.Properties[PropertyNames.Order][0];

                return xKey.CompareTo(yKey);
            }
        }
    }
}
