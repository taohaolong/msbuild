// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//-----------------------------------------------------------------------
// </copyright>
// <summary>Unit tests for the task builder object.</summary>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

using ElementLocation = Microsoft.Build.Construction.ElementLocation;
using ILoggingService = Microsoft.Build.BackEnd.Logging.ILoggingService;
using LegacyThreadingData = Microsoft.Build.Execution.LegacyThreadingData;
using TargetDotNetFrameworkVersion = Microsoft.Build.Utilities.TargetDotNetFrameworkVersion;
using ToolLocationHelper = Microsoft.Build.Utilities.ToolLocationHelper;
using Xunit;

namespace Microsoft.Build.UnitTests.BackEnd
{
    /// <summary>
    /// Unit tests for the TaskBuilder component
    /// </summary>
    public class TaskBuilder_Tests : ITargetBuilderCallback
    {
        /// <summary>
        /// Task definition for a task that outputs items containing null metadata.
        /// </summary>
        private static string s_nullMetadataTaskContents =
@"using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Collections;
using System.Collections.Generic;

namespace NullMetadataTask
{
    public class NullMetadataTask : Task
    {
        [Output]
        public ITaskItem[] OutputItems
        {
            get;
            set;
        }

        public override bool Execute()
        {
            OutputItems = new ITaskItem[1];

            IDictionary<string, string> metadata = new Dictionary<string, string>();
            metadata.Add(""a"", null);

            OutputItems[0] = new TaskItem(""foo"", (IDictionary)metadata);

            return true;
        }
    }
}
";

        /// <summary>
        /// Task definition for task that outputs items in a variety of ways, used to 
        /// test definition of the DefiningProject* metadata for task outputs. 
        /// </summary>
        private static string s_itemCreationTaskContents =
            @"using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Collections;
using System.Collections.Generic;

namespace ItemCreationTask
{
    public class ItemCreationTask : Task
    {
        public ITaskItem[] InputItemsToPassThrough
        {
            get;
            set;
        }

        public ITaskItem[] InputItemsToCopy
        {
            get;
            set;
        }

        [Output]
        public ITaskItem[] PassedThroughOutputItems
        {
            get;
            set;
        }

        [Output]
        public ITaskItem[] CreatedOutputItems
        {
            get;
            set;
        }

        [Output]
        public ITaskItem[] CopiedOutputItems
        {
            get;
            set;
        }

        [Output]
        public string OutputString
        {
            get;
            set;
        }

        public override bool Execute()
        {
            PassedThroughOutputItems = InputItemsToPassThrough;

            CopiedOutputItems = new ITaskItem[InputItemsToCopy.Length];

            for (int i = 0; i < InputItemsToCopy.Length; i++)
            {
                CopiedOutputItems[i] = new TaskItem(InputItemsToCopy[i]);
            }

            CreatedOutputItems = new ITaskItem[2];
            CreatedOutputItems[0] = new TaskItem(""Foo"");
            CreatedOutputItems[1] = new TaskItem(""Bar"");

            OutputString = ""abc;def;ghi"";

            return true;
        }
    }
}
";

        /// <summary>
        /// The mock component host and logger
        /// </summary>
        private MockHost _host;

        /// <summary>
        /// The temporary project we use to run the test
        /// </summary>
        private ProjectInstance _testProject;

        /// <summary>
        /// Prepares the environment for the test.
        /// </summary>
        public TaskBuilder_Tests()
        {
            _host = new MockHost();
            _testProject = CreateTestProject();
        }

        /*********************************************************************************
         * 
         *                                  OUTPUT PARAMS
         * 
         *********************************************************************************/

        /// <summary>
        /// Verifies that we do look up the task during execute when the condition is true.
        /// </summary>
        [Fact]
        public void TasksAreDiscoveredWhenTaskConditionTrue()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(
                @"<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                      <Target Name='t'>
                         <NonExistantTask Condition=""'1'=='1'""/>
                         <Message Text='Made it'/>                    
                      </Target>
                      </Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            project.Build("t", loggers);

            logger.AssertLogContains("MSB4036");
            logger.AssertLogDoesntContain("Made it");
        }

        /// <summary>
        /// Tests that when the task condition is false, Execute still returns true even though we never loaded
        /// the task.  We verify that we never loaded the task because if we did try, the task load itself would
        /// have failed, resulting in an error.
        /// </summary>
        [Fact]
        public void TasksNotDiscoveredWhenTaskConditionFalse()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(
                @"<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                      <Target Name='t'>
                         <NonExistantTask Condition=""'1'=='2'""/>
                         <Message Text='Made it'/>                    
                      </Target>
                      </Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            project.Build("t", loggers);

            logger.AssertLogContains("Made it");
        }

        /// <summary>
        /// Verify when task outputs are overridden the override messages are correctly displayed
        /// </summary>
        [Fact]
        public void OverridePropertiesInCreateProperty()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(
                @"<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                      <ItemGroup>
                         <EmbeddedResource Include='a.resx'>
                            <LogicalName>foo</LogicalName>
                         </EmbeddedResource>
                         <EmbeddedResource Include='b.resx'>
                            <LogicalName>bar</LogicalName>
                         </EmbeddedResource>
                         <EmbeddedResource Include='c.resx'>
                            <LogicalName>barz</LogicalName>
                         </EmbeddedResource>
                      </ItemGroup>
                      <Target Name='t'>
                         <CreateProperty Value=""@(EmbeddedResource->'/assemblyresource:%(Identity),%(LogicalName)', ' ')""
                                         Condition=""'%(LogicalName)' != '' "">
                             <Output TaskParameter=""Value"" PropertyName=""LinkSwitches""/>
                         </CreateProperty>
                         <Message Text='final:[$(LinkSwitches)]'/>                    
                      </Target>
                      </Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            project.Build("t", loggers);

            logger.AssertLogContains(new string[] { "final:[/assemblyresource:c.resx,barz]" });
            logger.AssertLogContains(new string[] { ResourceUtilities.FormatResourceString("TaskStarted", "CreateProperty") });
            logger.AssertLogContains(new string[] { ResourceUtilities.FormatResourceString("PropertyOutputOverridden", "LinkSwitches", "/assemblyresource:a.resx,foo", "/assemblyresource:b.resx,bar") });
            logger.AssertLogContains(new string[] { ResourceUtilities.FormatResourceString("PropertyOutputOverridden", "LinkSwitches", "/assemblyresource:b.resx,bar", "/assemblyresource:c.resx,barz") });
        }

        /// <summary>
        /// Verify that when a task outputs are inferred the override messages are displayed
        /// </summary>
        [Fact]
        public void OverridePropertiesInInferredCreateProperty()
        {
            string[] files = null;
            try
            {
                files = ObjectModelHelpers.GetTempFiles(2, new DateTime(2005, 1, 1));

                MockLogger logger = new MockLogger();
                string projectFileContents = ObjectModelHelpers.CleanupFileContents(
                    @"<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                      <ItemGroup>
                        <i Include='" + files[0] + "'><output>" + files[1] + @"</output></i>
                      </ItemGroup> 
                      <ItemGroup>
                         <EmbeddedResource Include='a.resx'>
                        <LogicalName>foo</LogicalName>
                          </EmbeddedResource>
                            <EmbeddedResource Include='b.resx'>
                            <LogicalName>bar</LogicalName>
                        </EmbeddedResource>
                            <EmbeddedResource Include='c.resx'>
                            <LogicalName>barz</LogicalName>
                        </EmbeddedResource>
                        </ItemGroup>
                      <Target Name='t2' DependsOnTargets='t'>
                        <Message Text='final:[$(LinkSwitches)]'/>   
                      </Target>
                      <Target Name='t' Inputs='%(i.Identity)' Outputs='%(i.Output)'>
                        <Message Text='start:[Hello]'/>
                        <CreateProperty Value=""@(EmbeddedResource->'/assemblyresource:%(Identity),%(LogicalName)', ' ')""
                                         Condition=""'%(LogicalName)' != '' "">
                             <Output TaskParameter=""Value"" PropertyName=""LinkSwitches""/>
                        </CreateProperty>
                        <Message Text='end:[hello]'/>                    
                    </Target>
                    </Project>");

                Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
                List<ILogger> loggers = new List<ILogger>();
                loggers.Add(logger);
                project.Build("t2", loggers);

                // We should only see messages from the second target, as the first is only inferred
                logger.AssertLogDoesntContain("start:");
                logger.AssertLogDoesntContain("end:");

                logger.AssertLogContains(new string[] { "final:[/assemblyresource:c.resx,barz]" });
                logger.AssertLogDoesntContain(ResourceUtilities.FormatResourceString("TaskStarted", "CreateProperty"));
                logger.AssertLogContains(new string[] { ResourceUtilities.FormatResourceString("PropertyOutputOverridden", "LinkSwitches", "/assemblyresource:a.resx,foo", "/assemblyresource:b.resx,bar") });
                logger.AssertLogContains(new string[] { ResourceUtilities.FormatResourceString("PropertyOutputOverridden", "LinkSwitches", "/assemblyresource:b.resx,bar", "/assemblyresource:c.resx,barz") });
            }
            finally
            {
                ObjectModelHelpers.DeleteTempFiles(files);
            }
        }

        /// <summary>
        /// Tests that tasks batch on outputs correctly.
        /// </summary>
        [Fact]
        public void TaskOutputBatching()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <TaskParameterItem Include=""foo"">
                            <ParameterName>Value</ParameterName>
                            <ParameterName2>Include</ParameterName2>
                            <PropertyName>MetadataProperty</PropertyName>
                            <ItemType>MetadataItem</ItemType>
                        </TaskParameterItem>
                    </ItemGroup>
                    <Target Name='Build'>
                        <CreateProperty Value=""@(TaskParameterItem)"">
                            <Output TaskParameter=""Value"" PropertyName=""Property1""/>
                        </CreateProperty> 
                        <Message Text='Property1=[$(Property1)]' />

                        <CreateProperty Value=""@(TaskParameterItem)"">
                            <Output TaskParameter=""%(TaskParameterItem.ParameterName)"" PropertyName=""Property2""/>
                        </CreateProperty> 
                        <Message Text='Property2=[$(Property2)]' />

                        <CreateProperty Value=""@(TaskParameterItem)"">
                            <Output TaskParameter=""Value"" PropertyName=""%(TaskParameterItem.PropertyName)""/>
                        </CreateProperty> 
                        <Message Text='MetadataProperty=[$(MetadataProperty)]' />

                        <CreateItem Include=""@(TaskParameterItem)"">
                            <Output TaskParameter=""Include"" ItemName=""TestItem1""/>
                        </CreateItem>
                        <Message Text='TestItem1=[@(TestItem1)]' />

                        <CreateItem Include=""@(TaskParameterItem)"">
                            <Output TaskParameter=""%(TaskParameterItem.ParameterName2)"" ItemName=""TestItem2""/>
                        </CreateItem>
                        <Message Text='TestItem2=[@(TestItem2)]' />

                        <CreateItem Include=""@(TaskParameterItem)"">
                            <Output TaskParameter=""Include"" ItemName=""%(TaskParameterItem.ItemType)""/>
                        </CreateItem>
                        <Message Text='MetadataItem=[@(MetadataItem)]' />
                    </Target>
                </Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            project.Build(loggers);

            logger.AssertLogContains("Property1=[foo]");
            logger.AssertLogContains("Property2=[foo]");
            logger.AssertLogContains("MetadataProperty=[foo]");
            logger.AssertLogContains("TestItem1=[foo]");
            logger.AssertLogContains("TestItem2=[foo]");
            logger.AssertLogContains("MetadataItem=[foo]");
        }

        /// <summary>
        /// MSbuildLastTaskResult property contains true or false indicating
        /// the success or failure of the last task.
        /// </summary>
        [Fact]
        public void MSBuildLastTaskResult()
        {
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
<Project DefaultTargets='t2' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
    <Target Name='t'>
        <Message Text='[start:$(MSBuildLastTaskResult)]'/> <!-- Should be blank -->
        <Warning Text='warning'/>
        <Message Text='[0:$(MSBuildLastTaskResult)]'/> <!-- Should be true, only a warning-->
        <!-- task's Execute returns false -->
        <Copy SourceFiles='|' DestinationFolder='c:\' ContinueOnError='true' />
        <PropertyGroup>
           <p>$(MSBuildLastTaskResult)</p>
        </PropertyGroup>                 
        <Message Text='[1:$(MSBuildLastTaskResult)]'/> <!-- Should be false: propertygroup did not reset it -->   
        <Message Text='[p:$(p)]'/> <!-- Should be false as stored earlier -->   
        <Message Text='[2:$(MSBuildLastTaskResult)]'/> <!-- Message succeeded, should now be true -->
    </Target>
    <Target Name='t2' DependsOnTargets='t'>
        <Message Text='[3:$(MSBuildLastTaskResult)]'/> <!-- Should still have true -->
        <!-- check Error task as well -->
        <Error Text='error' ContinueOnError='true' />
        <Message Text='[4:$(MSBuildLastTaskResult)]'/> <!-- Should be false -->
        <!-- trigger OnError target, ContinueOnError is false -->
        <Error Text='error2'/>
        <OnError ExecuteTargets='t3'/>
    </Target>
    <Target Name='t3' >
        <Message Text='[5:$(MSBuildLastTaskResult)]'/> <!-- Should be false -->
    </Target>
</Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            MockLogger logger = new MockLogger();
            loggers.Add(logger);
            project.Build("t2", loggers);

            logger.AssertLogContains("[start:]");
            logger.AssertLogContains("[0:true]");
            logger.AssertLogContains("[1:false]");
            logger.AssertLogContains("[p:false]");
            logger.AssertLogContains("[2:true]");
            logger.AssertLogContains("[3:true]");
            logger.AssertLogContains("[4:false]");
            logger.AssertLogContains("[4:false]");
        }

        /// <summary>
        /// Verifies that we can add "recursivedir" built-in metadata as target outputs. 
        /// This is to support wildcards in CreateItem. Allowing anything
        /// else could let the item get corrupt (inconsistent values for Filename and FullPath, for example)
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void TasksCanAddRecursiveDirBuiltInMetadata()
        {
            MockLogger logger = new MockLogger();

            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
<Target Name='t'>
 <CreateItem Include='$(programfiles)\reference assemblies\**\*.dll;'>
   <Output TaskParameter='Include' ItemName='x' />
 </CreateItem>
<Message Text='@(x)'/>
 <Message Text='[%(x.RecursiveDir)]'/>                    
</Target>
</Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            bool result = project.Build("t", loggers);

            Assert.Equal(true, result);
            logger.AssertLogDoesntContain("[]");
            logger.AssertLogDoesntContain("MSB4118");
            logger.AssertLogDoesntContain("MSB3031");
        }

        /// <summary>
        /// Verify CreateItem prevents adding any built-in metadata explicitly, even recursivedir.
        /// </summary>
        [Fact]
        public void OtherBuiltInMetadataErrors()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
<Target Name='t'>
 <CreateItem Include='Foo' AdditionalMetadata='RecursiveDir=1'>
   <Output TaskParameter='Include' ItemName='x' />
 </CreateItem>
</Target>
</Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            bool result = project.Build("t", loggers);

            Assert.Equal(false, result);
            logger.AssertLogContains("MSB3031");
        }

        /// <summary>
        /// Verify CreateItem prevents adding any built-in metadata explicitly, even recursivedir.
        /// </summary>
        [Fact]
        public void OtherBuiltInMetadataErrors2()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
<Target Name='t'>
 <CreateItem Include='Foo' AdditionalMetadata='Extension=1'/>
</Target>
</Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            bool result = project.Build("t", loggers);

            Assert.Equal(false, result);
            logger.AssertLogContains("MSB3031");
        }

        /// <summary>
        /// Verify that properties can be passed in to a task and out as items, despite the 
        /// built-in metadata restrictions.
        /// </summary>
        [Fact]
        public void PropertiesInItemsOutOfTask()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
<Target Name='t'>
 <PropertyGroup>
   <p>c:\a.ext</p>
 </PropertyGroup>
 <CreateItem Include='$(p)'>
   <Output TaskParameter='Include' ItemName='x' />
 </CreateItem>
 <Message Text='[%(x.Extension)]'/>
</Target>
</Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            bool result = project.Build("t", loggers);

            Assert.Equal(true, result);
            logger.AssertLogContains("[.ext]");
        }

#if FEATURE_CODEDOM
        /// <summary>
        /// Verify that properties can be passed in to a task and out as items, despite
        /// having illegal characters for a file name
        /// </summary>
        [Fact]
        public void IllegalFileCharsInItemsOutOfTask()
        {
            MockLogger logger = new MockLogger();
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
<Target Name='t'>
 <PropertyGroup>
   <p>||illegal||</p>
 </PropertyGroup>
 <CreateItem Include='$(p)'>
   <Output TaskParameter='Include' ItemName='x' />
 </CreateItem>
 <Message Text='[@(x)]'/>
</Target>
</Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            bool result = project.Build("t", loggers);

            Assert.Equal(true, result);
            logger.AssertLogContains("[||illegal||]");
        }

        /// <summary>
        /// If an item being output from a task has null metadata, we shouldn't crash. 
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void NullMetadataOnOutputItems()
        {
            string customTaskPath = CustomTaskHelper.GetAssemblyForTask(s_nullMetadataTaskContents);

            string projectContents = @"<Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
  <UsingTask TaskName=`NullMetadataTask` AssemblyFile=`" + customTaskPath + @"` />

  <Target Name=`Build`>
    <NullMetadataTask>
      <Output TaskParameter=`OutputItems` ItemName=`Outputs`/>
    </NullMetadataTask>

    <Message Text=`[%(Outputs.Identity): %(Outputs.a)]` Importance=`High` />
  </Target>
</Project>";

            MockLogger logger = ObjectModelHelpers.BuildProjectExpectSuccess(projectContents);
            logger.AssertLogContains("[foo: ]");
        }

        /// <summary>
        /// If an item being output from a task has null metadata, we shouldn't crash. 
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void NullMetadataOnLegacyOutputItems()
        {
            string referenceAssembliesPath = ToolLocationHelper.GetPathToDotNetFrameworkReferenceAssemblies(TargetDotNetFrameworkVersion.VersionLatest);

            if (String.IsNullOrEmpty(referenceAssembliesPath))
            {
                // fall back to the .NET Framework -- they should always exist there. 
                referenceAssembliesPath = ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.VersionLatest);
            }

            string[] referenceAssemblies = new string[] { Path.Combine(referenceAssembliesPath, "System.dll"), Path.Combine(referenceAssembliesPath, "Microsoft.Build.Framework.dll"), Path.Combine(referenceAssembliesPath, "Microsoft.Build.Utilities.v4.0.dll") };

            string customTaskPath = CustomTaskHelper.GetAssemblyForTask(s_nullMetadataTaskContents, referenceAssemblies);

            string projectContents = @"<Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
  <UsingTask TaskName=`NullMetadataTask` AssemblyFile=`" + customTaskPath + @"` />

  <Target Name=`Build`>
    <NullMetadataTask>
      <Output TaskParameter=`OutputItems` ItemName=`Outputs`/>
    </NullMetadataTask>

    <Message Text=`[%(Outputs.Identity): %(Outputs.a)]` Importance=`High` />
  </Target>
</Project>";

            MockLogger logger = ObjectModelHelpers.BuildProjectExpectSuccess(projectContents);
            logger.AssertLogContains("[foo: ]");
        }
#endif

#if FEATURE_CODETASKFACTORY
        /// <summary>
        /// If an item being output from a task has null metadata, we shouldn't crash. 
        /// </summary>
        [Fact]
        public void NullMetadataOnOutputItems_InlineTask()
        {
            string projectContents = @"
                    <Project xmlns='msbuildnamespace' ToolsVersion='msbuilddefaulttoolsversion'>
                        <UsingTask TaskName=`NullMetadataTask_v12` TaskFactory=`CodeTaskFactory` AssemblyFile=`$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll`>
                            <ParameterGroup>
                               <OutputItems ParameterType=`Microsoft.Build.Framework.ITaskItem[]` Output=`true` />
                            </ParameterGroup>
                            <Task>
                                <Code>
                                <![CDATA[
                                    OutputItems = new ITaskItem[1];

                                    IDictionary<string, string> metadata = new Dictionary<string, string>();
                                    metadata.Add(`a`, null);

                                    OutputItems[0] = new TaskItem(`foo`, (IDictionary)metadata);

                                    return true;
                                ]]>
                                </Code>
                            </Task>
                        </UsingTask>
                      <Target Name=`Build`>
                        <NullMetadataTask_v12>
                          <Output TaskParameter=`OutputItems` ItemName=`Outputs` />
                        </NullMetadataTask_v12>

                        <Message Text=`[%(Outputs.Identity): %(Outputs.a)]` Importance=`High` />
                      </Target>
                    </Project>";

            MockLogger logger = ObjectModelHelpers.BuildProjectExpectSuccess(projectContents);
            logger.AssertLogContains("[foo: ]");
        }

        /// <summary>
        /// If an item being output from a task has null metadata, we shouldn't crash. 
        /// </summary>
        [Fact]
        [Trait("Category", "non-mono-tests")]
        public void NullMetadataOnLegacyOutputItems_InlineTask()
        {
            string projectContents = @"
                    <Project xmlns='msbuildnamespace' ToolsVersion='msbuilddefaulttoolsversion'>
                        <UsingTask TaskName=`NullMetadataTask_v4` TaskFactory=`CodeTaskFactory` AssemblyFile=`$(MSBuildFrameworkToolsPath)\Microsoft.Build.Tasks.v4.0.dll`>
                            <ParameterGroup>
                               <OutputItems ParameterType=`Microsoft.Build.Framework.ITaskItem[]` Output=`true` />
                            </ParameterGroup>
                            <Task>
                                <Code>
                                <![CDATA[
                                    OutputItems = new ITaskItem[1];

                                    IDictionary<string, string> metadata = new Dictionary<string, string>();
                                    metadata.Add(`a`, null);

                                    OutputItems[0] = new TaskItem(`foo`, (IDictionary)metadata);

                                    return true;
                                ]]>
                                </Code>
                            </Task>
                        </UsingTask>
                      <Target Name=`Build`>
                        <NullMetadataTask_v4>
                          <Output TaskParameter=`OutputItems` ItemName=`Outputs` />
                        </NullMetadataTask_v4>

                        <Message Text=`[%(Outputs.Identity): %(Outputs.a)]` Importance=`High` />
                      </Target>
                    </Project>";

            MockLogger logger = ObjectModelHelpers.BuildProjectExpectSuccess(projectContents);
            logger.AssertLogContains("[foo: ]");
        }
#endif

#if FEATURE_CODEDOM
        /// <summary>
        /// Validates that the defining project metadata is set (or not set) as expected in 
        /// various task output-related operations, using a task built against the current 
        /// version of MSBuild.  
        /// </summary>
        public void ValidateDefiningProjectMetadataOnTaskOutputs()
        {
            string customTaskPath = CustomTaskHelper.GetAssemblyForTask(s_itemCreationTaskContents);
            ValidateDefiningProjectMetadataOnTaskOutputsHelper(customTaskPath);
        }

        /// <summary>
        /// Validates that the defining project metadata is set (or not set) as expected in 
        /// various task output-related operations, using a task built against V4 MSBuild, 
        /// which didn't support the defining project metadata.  
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void ValidateDefiningProjectMetadataOnTaskOutputs_LegacyItems()
        {
            string referenceAssembliesPath = ToolLocationHelper.GetPathToDotNetFrameworkReferenceAssemblies(TargetDotNetFrameworkVersion.VersionLatest);

            if (String.IsNullOrEmpty(referenceAssembliesPath))
            {
                referenceAssembliesPath = ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.VersionLatest);
            }

            string[] referenceAssemblies = new string[] { Path.Combine(referenceAssembliesPath, "System.dll"), Path.Combine(referenceAssembliesPath, "Microsoft.Build.Framework.dll"), Path.Combine(referenceAssembliesPath, "Microsoft.Build.Utilities.v4.0.dll") };

            string customTaskPath = CustomTaskHelper.GetAssemblyForTask(s_itemCreationTaskContents, referenceAssemblies);
            ValidateDefiningProjectMetadataOnTaskOutputsHelper(customTaskPath);
        }
#endif

#if FEATURE_APARTMENT_STATE
        /// <summary>
        /// Tests that putting the RunInSTA attribute on a task causes it to run in the STA thread.
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void TestSTAThreadRequired()
        {
            TestSTATask(true, false, false);
        }

        /// <summary>
        /// Tests an STA task with an exception
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void TestSTAThreadRequiredWithException()
        {
            TestSTATask(true, false, true);
        }

        /// <summary>
        /// Tests an STA task with failure.
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void TestSTAThreadRequiredWithFailure()
        {
            TestSTATask(true, true, false);
        }

        /// <summary>
        /// Tests an MTA task.
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void TestSTAThreadNotRequired()
        {
            TestSTATask(false, false, false);
        }

        /// <summary>
        /// Tests an MTA task with an exception.
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void TestSTAThreadNotRequiredWithException()
        {
            TestSTATask(false, false, true);
        }

        /// <summary>
        /// Tests an MTA task with failure.
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void TestSTAThreadNotRequiredWithFailure()
        {
            TestSTATask(false, true, false);
        }
#endif

        #region ITargetBuilderCallback Members

        /// <summary>
        /// Empty impl
        /// </summary>
        Task<ITargetResult[]> ITargetBuilderCallback.LegacyCallTarget(string[] targets, bool continueOnError, ElementLocation referenceLocation)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Empty impl
        /// </summary>
        void IRequestBuilderCallback.Yield()
        {
        }

        /// <summary>
        /// Empty impl
        /// </summary>
        void IRequestBuilderCallback.Reacquire()
        {
        }

        /// <summary>
        /// Empty impl
        /// </summary>
        void IRequestBuilderCallback.EnterMSBuildCallbackState()
        {
        }

        /// <summary>
        /// Empty impl
        /// </summary>
        void IRequestBuilderCallback.ExitMSBuildCallbackState()
        {
        }

        #endregion

        #region IRequestBuilderCallback Members

        /// <summary>
        /// Empty impl
        /// </summary>
        Task<BuildResult[]> IRequestBuilderCallback.BuildProjects(string[] projectFiles, PropertyDictionary<ProjectPropertyInstance>[] properties, string[] toolsVersions, string[] targets, bool waitForResults)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented.
        /// </summary>
        Task IRequestBuilderCallback.BlockOnTargetInProgress(int blockingRequestId, string blockingTarget)
        {
            throw new NotImplementedException();
        }

        #endregion

        /*********************************************************************************
         * 
         *                                     Helpers
         * 
         *********************************************************************************/

        /// <summary>
        /// Helper method for validating the setting of defining project metadata on items 
        /// coming from task outputs
        /// </summary>
        private void ValidateDefiningProjectMetadataOnTaskOutputsHelper(string customTaskPath)
        {
            string projectAPath = Path.Combine(ObjectModelHelpers.TempProjectDir, "a.proj");
            string projectBPath = Path.Combine(ObjectModelHelpers.TempProjectDir, "b.proj");

            string projectAContents = @"
                <Project xmlns=`msbuildnamespace` ToolsVersion=`msbuilddefaulttoolsversion`>
                    <UsingTask TaskName=`ItemCreationTask` AssemblyFile=`" + customTaskPath + @"` />
                    <Import Project=`b.proj` />

                    <Target Name=`Run`>
                      <ItemCreationTask 
                        InputItemsToPassThrough=`@(PassThrough)`
                        InputItemsToCopy=`@(Copy)`>
                          <Output TaskParameter=`OutputString` ItemName=`A` />
                          <Output TaskParameter=`PassedThroughOutputItems` ItemName=`B` />
                          <Output TaskParameter=`CreatedOutputItems` ItemName=`C` />
                          <Output TaskParameter=`CopiedOutputItems` ItemName=`D` />
                      </ItemCreationTask>

                      <Warning Text=`A is wrong: EXPECTED: [a] ACTUAL: [%(A.DefiningProjectName)]` Condition=`'%(A.DefiningProjectName)' != 'a'` />    
                      <Warning Text=`B is wrong: EXPECTED: [a] ACTUAL: [%(B.DefiningProjectName)]` Condition=`'%(B.DefiningProjectName)' != 'a'` />    
                      <Warning Text=`C is wrong: EXPECTED: [a] ACTUAL: [%(C.DefiningProjectName)]` Condition=`'%(C.DefiningProjectName)' != 'a'` />    
                      <Warning Text=`D is wrong: EXPECTED: [a] ACTUAL: [%(D.DefiningProjectName)]` Condition=`'%(D.DefiningProjectName)' != 'a'` />    
                    </Target>
                </Project>
";

            string projectBContents = @"
                <Project xmlns=`msbuildnamespace` ToolsVersion=`msbuilddefaulttoolsversion`>

                    <ItemGroup>
                        <PassThrough Include=`aaa.cs` />
                        <Copy Include=`bbb.cs` />
                    </ItemGroup>
                </Project>
";

            try
            {
                File.WriteAllText(projectAPath, ObjectModelHelpers.CleanupFileContents(projectAContents));
                File.WriteAllText(projectBPath, ObjectModelHelpers.CleanupFileContents(projectBContents));

                MockLogger logger = ObjectModelHelpers.BuildTempProjectFileExpectSuccess("a.proj");
                logger.AssertNoWarnings();
            }
            finally
            {
                if (File.Exists(projectAPath))
                {
                    File.Delete(projectAPath);
                }

                if (File.Exists(projectBPath))
                {
                    File.Delete(projectBPath);
                }
            }
        }

#if FEATURE_APARTMENT_STATE
        /// <summary>
        /// Executes an STA task test.
        /// </summary>
        private void TestSTATask(bool requireSTA, bool failTask, bool throwException)
        {
            MockLogger logger = new MockLogger();
            logger.AllowTaskCrashes = throwException;

            string taskAssemblyName = null;
            Project project = CreateSTATestProject(requireSTA, failTask, throwException, out taskAssemblyName);

            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);

            BuildParameters parameters = new BuildParameters();
            parameters.Loggers = new ILogger[] { logger };
            BuildResult result = BuildManager.DefaultBuildManager.Build(parameters, new BuildRequestData(project.CreateProjectInstance(), new string[] { "Foo" }));
            if (requireSTA)
            {
                logger.AssertLogContains("STA");
            }
            else
            {
                logger.AssertLogContains("MTA");
            }

            if (throwException)
            {
                logger.AssertLogContains("EXCEPTION");
                Assert.Equal(BuildResultCode.Failure, result.OverallResult);
                return;
            }
            else
            {
                logger.AssertLogDoesntContain("EXCEPTION");
            }

            if (failTask)
            {
                logger.AssertLogContains("FAIL");
                Assert.Equal(BuildResultCode.Failure, result.OverallResult);
            }
            else
            {
                logger.AssertLogDoesntContain("FAIL");
            }

            if (!throwException && !failTask)
            {
                Assert.Equal(BuildResultCode.Success, result.OverallResult);
            }
        }

        /// <summary>
        /// Helper to create a project which invokes the STA helper task.
        /// </summary>
        private Project CreateSTATestProject(bool requireSTA, bool failTask, bool throwException, out string assemblyToDelete)
        {
            assemblyToDelete = GenerateSTATask(requireSTA);

            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
<UsingTask TaskName='ThreadTask' AssemblyFile='" + assemblyToDelete + @"'/>
	<Target Name='Foo'>
		<ThreadTask Fail='" + failTask + @"' ThrowException='" + throwException + @"'/>
	</Target>
</Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));

            return project;
        }
#endif

#if FEATURE_CODEDOM
        /// <summary>
        /// Helper to create the STA test task.
        /// </summary>
        private string GenerateSTATask(bool requireSTA)
        {
            string taskContents =
                @"
using System;
using Microsoft.Build.Framework;
namespace ClassLibrary2
{" + (requireSTA ? "[RunInSTA]" : String.Empty) + @"
    public class ThreadTask : ITask
    {
        #region ITask Members

        public IBuildEngine BuildEngine
        {
            get;
            set;
        }

        public bool ThrowException
        {
            get;
            set;
        }

        public bool Fail
        {
            get;
            set;
        }

        public bool Execute()
        {
            string message;
            if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA)
            {
                message = ""STA"";
            }
            else
            {
                message = ""MTA"";
            }

            BuildEngine.LogMessageEvent(new BuildMessageEventArgs(message, """", ""ThreadTask"", MessageImportance.High));

            if (ThrowException)
            {
                throw new InvalidOperationException(""EXCEPTION"");
            }

            if (Fail)
            {
                BuildEngine.LogMessageEvent(new BuildMessageEventArgs(""FAIL"", """", ""ThreadTask"", MessageImportance.High));
            }

            return !Fail;
        }

        public ITaskHost HostObject
        {
            get;
            set;
        }

        #endregion
    }
}";
            return CustomTaskHelper.GetAssemblyForTask(taskContents);
        }
#endif

        /// <summary>
        /// Creates a test project.
        /// </summary>
        /// <returns>The project.</returns>
        private ProjectInstance CreateTestProject()
        {
            string projectFileContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>

                    <ItemGroup>
                        <Compile Include='b.cs' />
                        <Compile Include='c.cs' />
                    </ItemGroup>

                    <ItemGroup>
                        <Reference Include='System' />
                    </ItemGroup>

                    <Target Name='Empty' />

                    <Target Name='Skip' Inputs='testProject.proj' Outputs='testProject.proj' />

                    <Target Name='Error' >
                        <ErrorTask1 ContinueOnError='True'/>                    
                        <ErrorTask2 ContinueOnError='False'/>  
                        <ErrorTask3 /> 
                        <OnError ExecuteTargets='Foo'/>                  
                        <OnError ExecuteTargets='Bar'/>                  
                    </Target>

                    <Target Name='Foo' Inputs='foo.cpp' Outputs='foo.o'>
                        <FooTask1/>
                    </Target>

                    <Target Name='Bar'>
                        <BarTask1/>
                    </Target>

                    <Target Name='Baz' DependsOnTargets='Bar'>
                        <BazTask1/>
                        <BazTask2/>
                    </Target>

                    <Target Name='Baz2' DependsOnTargets='Bar;Foo'>
                        <Baz2Task1/>
                        <Baz2Task2/>
                        <Baz2Task3/>
                    </Target>

                    <Target Name='DepSkip' DependsOnTargets='Skip'>
                        <DepSkipTask1/>
                        <DepSkipTask2/>
                        <DepSkipTask3/>
                    </Target>

                    <Target Name='DepError' DependsOnTargets='Foo;Skip;Error'>
                        <DepSkipTask1/>
                        <DepSkipTask2/>
                        <DepSkipTask3/>
                    </Target>

                </Project>
                ");

            IConfigCache cache = (IConfigCache)_host.GetComponent(BuildComponentType.ConfigCache);
            BuildRequestConfiguration config = new BuildRequestConfiguration(1, new BuildRequestData("testfile", new Dictionary<string, string>(), "3.5", new string[0], null), "2.0");
            Project project = new Project(XmlReader.Create(new StringReader(projectFileContents)));
            config.Project = project.CreateProjectInstance();
            cache.AddConfiguration(config);

            return config.Project;
        }

        /// <summary>
        /// The mock component host object.
        /// </summary>
        private class MockHost : MockLoggingService, IBuildComponentHost, IBuildComponent
        {
            #region IBuildComponentHost Members

            /// <summary>
            /// The config cache
            /// </summary>
            private IConfigCache _configCache;

            /// <summary>
            /// The logging service
            /// </summary>
            private ILoggingService _loggingService;

            /// <summary>
            /// The results cache
            /// </summary>
            private IResultsCache _resultsCache;

            /// <summary>
            /// The request builder
            /// </summary>
            private IRequestBuilder _requestBuilder;

            /// <summary>
            /// The target builder
            /// </summary>
            private ITargetBuilder _targetBuilder;

            /// <summary>
            /// The build parameters.
            /// </summary>
            private BuildParameters _buildParameters;

            /// <summary>
            /// Retrieves the LegacyThreadingData associated with a particular component host
            /// </summary>
            private LegacyThreadingData _legacyThreadingData;

            /// <summary>
            /// Constructor
            /// 
            /// UNDONE: Refactor this, and the other MockHosts, to use a common base implementation.  The duplication of the
            /// logging implementation alone is unfortunate.
            /// </summary>
            public MockHost()
            {
                _buildParameters = new BuildParameters();
                _legacyThreadingData = new LegacyThreadingData();

                _configCache = new ConfigCache();
                ((IBuildComponent)_configCache).InitializeComponent(this);

                _loggingService = this;

                _resultsCache = new ResultsCache();
                ((IBuildComponent)_resultsCache).InitializeComponent(this);

                _requestBuilder = new RequestBuilder();
                ((IBuildComponent)_requestBuilder).InitializeComponent(this);

                _targetBuilder = new TargetBuilder();
                ((IBuildComponent)_targetBuilder).InitializeComponent(this);
            }

            /// <summary>
            /// Returns the node logging service.  We don't distinguish here.
            /// </summary>
            public ILoggingService LoggingService
            {
                get
                {
                    return _loggingService;
                }
            }

            /// <summary>
            /// Retrieves the name of the host.
            /// </summary>
            public string Name
            {
                get
                {
                    return "TaskBuilder_Tests.MockHost";
                }
            }

            /// <summary>
            /// Returns the build parameters.
            /// </summary>
            public BuildParameters BuildParameters
            {
                get
                {
                    return _buildParameters;
                }
            }

            /// <summary>
            /// Retrieves the LegacyThreadingData associated with a particular component host
            /// </summary>
            LegacyThreadingData IBuildComponentHost.LegacyThreadingData
            {
                get
                {
                    return _legacyThreadingData;
                }
            }

            /// <summary>
            /// Constructs and returns a component of the specified type.
            /// </summary>
            /// <param name="type">The type of component to return</param>
            /// <returns>The component</returns>
            public IBuildComponent GetComponent(BuildComponentType type)
            {
                switch (type)
                {
                    case BuildComponentType.ConfigCache:
                        return (IBuildComponent)_configCache;

                    case BuildComponentType.LoggingService:
                        return (IBuildComponent)_loggingService;

                    case BuildComponentType.ResultsCache:
                        return (IBuildComponent)_resultsCache;

                    case BuildComponentType.RequestBuilder:
                        return (IBuildComponent)_requestBuilder;

                    case BuildComponentType.TargetBuilder:
                        return (IBuildComponent)_targetBuilder;

                    default:
                        throw new ArgumentException("Unexpected type " + type);
                }
            }

            /// <summary>
            /// Register a component factory.
            /// </summary>
            public void RegisterFactory(BuildComponentType type, BuildComponentFactoryDelegate factory)
            {
            }

            #endregion

            #region IBuildComponent Members

            /// <summary>
            /// Sets the component host
            /// </summary>
            /// <param name="host">The component host</param>
            public void InitializeComponent(IBuildComponentHost host)
            {
                throw new NotImplementedException();
            }

            /// <summary>
            /// Shuts down the component
            /// </summary>
            public void ShutdownComponent()
            {
                throw new NotImplementedException();
            }

            #endregion
        }
    }
}
