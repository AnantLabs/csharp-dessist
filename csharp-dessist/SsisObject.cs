﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using System.Data;

namespace csharp_dessist
{
    public class SsisObject
    {
        /// <summary>
        /// The XML node type of this object
        /// </summary>
        public string DtsObjectType;

        /// <summary>
        /// The human readable name of this object
        /// </summary>
        public string DtsObjectName;
        private string _FunctionName;
        private string _FolderName;

        /// <summary>
        /// A user-readable explanation of what this is
        /// </summary>
        public string Description;

        /// <summary>
        /// The GUID for this object
        /// </summary>
        public Guid DtsId;

        /// <summary>
        /// Attributes, if any
        /// </summary>
        public Dictionary<string, string> Attributes = new Dictionary<string, string>();

        /// <summary>
        /// All the properties defined in the SSIS
        /// </summary>
        public Dictionary<string, string> Properties = new Dictionary<string, string>();

        /// <summary>
        /// List of child elements in SSIS
        /// </summary>
        public List<SsisObject> Children = new List<SsisObject>();
        public SsisObject Parent = null;

        /// <summary>
        /// Save the content value of a complex object
        /// </summary>
        public string ContentValue;

        private List<LineageObject> _lineage_columns = new List<LineageObject>();

        #region Shortcuts

        /// <summary>
        /// Set a property
        /// </summary>
        /// <param name="prop_name"></param>
        /// <param name="prop_value"></param>
        public void SetProperty(string prop_name, string prop_value)
        {
            if (prop_name == "ObjectName") {
                DtsObjectName = prop_value;
            } else if (prop_name == "DTSID") {
                DtsId = Guid.Parse(prop_value);
                _guid_lookup[DtsId] = this;
            } else if (prop_name == "Description") {
                Description = prop_value;
            } else {
                Properties[prop_name] = prop_value;
            }
        }

        /// <summary>
        /// Retrieve a child with the specific name
        /// </summary>
        /// <param name="objectname"></param>
        public SsisObject GetChildByType(string objectname)
        {
            return (from SsisObject o in Children where o.DtsObjectType == objectname select o).FirstOrDefault();
        }

        /// <summary>
        /// Retrieve a child with the specific name
        /// </summary>
        /// <param name="objectname"></param>
        public SsisObject GetChildByTypeAndAttr(string objectname, string attribute, string value)
        {
            return (from SsisObject o in Children 
                    where (o.DtsObjectType == objectname) 
                    && (o.Attributes[attribute] == value) 
                    select o).FirstOrDefault();
        }
        #endregion

        #region Translate this object into C# code
        /// <summary>
        /// Produce this variable to the current stream
        /// </summary>
        /// <param name="indent_depth"></param>
        /// <param name="as_global"></param>
        /// <param name="sw"></param>
        internal void EmitVariable(string indent, bool as_global, StreamWriter sw)
        {
            VariableData vd = new VariableData(this, as_global);

            // Do we add comments for these variables?
            string privilege = "";
            if (as_global) {
                if (!String.IsNullOrEmpty(vd.Comment)) {
                    sw.WriteLine();
                    sw.WriteLine("{0}/// <summary>", indent);
                    sw.WriteLine("{0}/// {1}", indent, vd.Comment);
                    sw.WriteLine("{0}/// </summary>", indent);
                }
                privilege = "public static ";
            }

            // Write it out
            if (String.IsNullOrEmpty(vd.DefaultValue)) {
                sw.WriteLine(String.Format(@"{0}{3}{2} {1};", indent, vd.VariableName, vd.CSharpType, privilege));
            } else {
                sw.WriteLine(String.Format(@"{0}{4}{3} {1} = {2};", indent, vd.VariableName, vd.DefaultValue, vd.CSharpType, privilege));
            }

            // Keep track of variables so we can do type conversions in the future!
            _var_dict[vd.VariableName] = vd;
        }
        protected static Dictionary<string, VariableData> _var_dict = new Dictionary<string, VariableData>();

        /// <summary>
        /// Produce this variable to the current stream
        /// </summary>
        /// <param name="indent_depth"></param>
        /// <param name="as_global"></param>
        /// <param name="sw"></param>
        internal void EmitFunction(string indent, StreamWriter sw)
        {
            // Header and comments
            sw.WriteLine();
            if (!String.IsNullOrEmpty(Description)) {
                sw.WriteLine("{0}/// <summary>", indent);
                sw.WriteLine("{0}/// {1}", indent, Description);
                sw.WriteLine("{0}/// </summary>", indent);
            }

            // Function intro
            sw.WriteLine(String.Format("{0}public static void {1}()", indent, GetFunctionName()));
            sw.WriteLine(String.Format("{0}{{", indent));

            // What type of executable are we?  Let's check if special handling is required
            string exec_type = Attributes["DTS:ExecutableType"];

            // Child script project - Emit it as a sub-project within the greater solution!
            if (exec_type.StartsWith("Microsoft.SqlServer.Dts.Tasks.ScriptTask.ScriptTask")) {
                ProjectWriter.EmitScriptProject(this, indent + "    ", sw);

            // Basic SQL command
            } else if (exec_type.StartsWith("Microsoft.SqlServer.Dts.Tasks.ExecuteSQLTask.ExecuteSQLTask")) {
                this.EmitSqlTask(indent, sw);

            // Basic "SEQUENCE" construct - just execute things in order!
            } else if (exec_type.StartsWith("STOCK:SEQUENCE")) {
                EmitChildObjects(indent, sw);

            // Handle "FOR" and "FOREACH" loop types
            } else if (exec_type == "STOCK:FORLOOP") {
                this.EmitForLoop(indent + "    ", sw);
            } else if (exec_type == "STOCK:FOREACHLOOP") {
                this.EmitForEachLoop(indent + "    ", sw);
            } else if (exec_type == "SSIS.Pipeline.2") {
                this.EmitPipeline(indent + "    ", sw);
            } else if (exec_type.StartsWith("Microsoft.SqlServer.Dts.Tasks.SendMailTask.SendMailTask")) {
                this.EmitSendMailTask(indent + "    ", sw);

            // Something I don't yet understand
            } else {
                HelpWriter.Help(this, "I don't yet know how to handle " + exec_type);
            }

            // TODO: Is there an exception handler?  How many types of event handlers are there?
            // TODO: Check precedence constraints
            // TODO: Create a general purpose lookup of DTSID objects

            // End of function
            sw.WriteLine("{0}}}", indent);

            // Now emit any other functions that are chained into this
            foreach (SsisObject o in Children) {
                if (o.DtsObjectType == "DTS:Executable") {
                    o.EmitFunction(indent, sw);
                }
            }
        }

        private void EmitSendMailTask(string indent, StreamWriter sw)
        {
            // Navigate to our object data
            SsisObject mail = GetChildByType("DTS:ObjectData").GetChildByType("SendMailTask:SendMailTaskData");

            sw.WriteLine(@"{0}MailMessage message = new MailMessage();", indent);
            sw.WriteLine(@"{0}message.To.Add(""{1}"");", indent, mail.Attributes["SendMailTask:To"]);
            sw.WriteLine(@"{0}message.Subject = ""{1}"";", indent, mail.Attributes["SendMailTask:Subject"]);
            sw.WriteLine(@"{0}message.From = new MailAddress(""{1}"");", indent, mail.Attributes["SendMailTask:From"]);
            
            // Handle CC/BCC if available
            string addr = null;
            if (mail.Attributes.TryGetValue("SendMailTask:CC", out addr) && !String.IsNullOrEmpty(addr)) {
                sw.WriteLine(@"{0}message.CC.Add(""{1}"");", indent, addr);
            }
            if (mail.Attributes.TryGetValue("SendMailTask:BCC", out addr) && !String.IsNullOrEmpty(addr)) {
                sw.WriteLine(@"{0}message.Bcc.Add(""{1}"");", indent, addr);
            }

            // Process the message source
            string sourcetype = mail.Attributes["SendMailTask:MessageSourceType"];
            if (sourcetype == "Variable") {
                sw.WriteLine(@"{0}message.Body = {1};", indent, FixVariableName(mail.Attributes["SendMailTask:MessageSource"]));
            } else if (sourcetype == "DirectInput") {
                sw.WriteLine(@"{0}message.Body = @""{1}"";", indent, mail.Attributes["SendMailTask:MessageSource"].Replace("\"", "\"\""));
            } else {
                HelpWriter.Help(this, "I don't understand the SendMail message source type '" + sourcetype + "'");
            }

            // Get the SMTP configuration name
            sw.WriteLine(@"{0}using (var smtp = new SmtpClient(ConfigurationManager.AppSettings[""{1}""])) {{", indent, GetObjectByGuid(mail.Attributes["SendMailTask:SMTPServer"]).DtsObjectName);
            sw.WriteLine(@"{0}    smtp.Send(message);", indent);
            sw.WriteLine(@"{0}}}", indent);
        }

        private void EmitSqlTask(string indent, StreamWriter sw)
        {
            EmitChildObjects(indent, sw);
        }

        private void EmitChildObjects(string indent, StreamWriter sw)
        {
            string newindent = indent + "    ";

            // To handle precedence data correctly, first make a list of encumbered children
            List<SsisObject> modified_children = new List<SsisObject>();
            modified_children.AddRange(Children);

            // Write comments for the precedence data - we'll eventually have to handle this
            List<PrecedenceData> precedence = new List<PrecedenceData>();
            foreach (SsisObject o in Children) {
                if (o.DtsObjectType == "DTS:PrecedenceConstraint") {
                    PrecedenceData pd = new PrecedenceData(o);

                    // Does this precedence data affect any children?  Find it and move it
                    var c = (from SsisObject obj in modified_children where obj.DtsId == pd.AfterGuid select obj).FirstOrDefault();
                    modified_children.Remove(c);

                    // Add it to the list
                    precedence.Add(pd);
                }
            }

            if (modified_children.Count > 0) {
                sw.WriteLine("{0}// These calls have no dependencies", newindent);

                // Function body
                foreach (SsisObject o in modified_children) {

                    // Are there any precedence triggers after this child?
                    PrecedenceChain(o, precedence, newindent, sw);
                }
            }
        }

        private void PrecedenceChain(SsisObject prior_obj, List<PrecedenceData> precedence, string indent, StreamWriter sw)
        {
            EmitOneChild(prior_obj, indent, sw);

            // We just executed "prior_obj" - find what objects it causes to be triggered
            var triggered = (from PrecedenceData pd in precedence where pd.BeforeGuid == prior_obj.DtsId select pd);

            // Iterate through each of these
            foreach (PrecedenceData pd in triggered) {

                // Write a comment
                sw.WriteLine();
                sw.WriteLine("{0}// {1}", indent, pd.ToString());

                // Is there an expression?
                if (!String.IsNullOrEmpty(pd.Expression)) {
                    sw.WriteLine(@"{0}if ({1}) {{", indent, FixExpression(pd.Expression));
                    PrecedenceChain(pd.Target, precedence, indent + "    ", sw);
                    sw.WriteLine(@"{0}}}", indent);
                } else {
                    PrecedenceChain(pd.Target, precedence, indent, sw);
                }
            }
        }

        private void EmitOneChild(SsisObject childobj, string newindent, StreamWriter sw)
        {
            // Is this a dummy "Object Data" thing?  If so ignore it and delve deeper
            if (childobj.DtsObjectType == "DTS:ObjectData") {
                childobj = childobj.Children[0];
            }

            // For variables, emit them within this function
            if (childobj.DtsObjectType == "DTS:Variable") {
                childobj.EmitVariable(newindent, false, sw);
            } else if (childobj.DtsObjectType == "DTS:Executable") {
                childobj.EmitFunctionCall(newindent, sw);
            } else if (childobj.DtsObjectType == "SQLTask:SqlTaskData") {
                childobj.EmitSqlStatement(newindent, sw);

                // TODO: Handle "pipeline" objects
            } else if (childobj.DtsObjectType == "pipeline") {
                childobj.EmitPipeline(newindent, sw);
            } else if (childobj.DtsObjectType == "DTS:PrecedenceConstraint") {
                // ignore it - it's already been handled
            } else if (childobj.DtsObjectType == "DTS:LoggingOptions") {
                // Ignore it - I can't figure out any useful information on this object
            } else if (childobj.DtsObjectType == "DTS:ForEachVariableMapping") {
                // ignore it - handled earlier
            } else if (childobj.DtsObjectType == "DTS:ForEachEnumerator") {
                // ignore it - handled explicitly by the foreachloop

            } else {
                HelpWriter.Help(this, "I don't yet know how to handle " + childobj.DtsObjectType);
            }
        }

        private void EmitForEachVariableMapping(string indent, StreamWriter sw)
        {
            string varname = FixVariableName(this.Properties["VariableName"]);

            // Look up the variable data
            VariableData vd = _var_dict[varname];

            // Produce a line
            sw.WriteLine(String.Format(@"{0}{1} = ({3})iter.ItemArray[{2}];", indent, varname, this.Properties["ValueIndex"], vd.CSharpType));
        }

        private void EmitForEachLoop(string indent, StreamWriter sw)
        {
            // Retrieve the three settings from the for loop
            //string init = this.Properties["InitExpression"];
            //string eval = this.Properties["EvalExpression"];
            //string assign = this.Properties["AssignExpression"];
            string iterator = FixVariableName(GetChildByType("DTS:ForEachEnumerator").GetChildByType("DTS:ObjectData").Children[0].Attributes["VarName"]);

            // Write it out - I'm assuming this is a data table for now
            sw.WriteLine(String.Format(@"{0}foreach (DataRow iter in {1}.Rows) {{", indent, iterator));
            sw.WriteLine();
            string newindent = indent + "    ";
            sw.WriteLine(String.Format(@"{0}// Setup all variable mappings", newindent, iterator));

            // Do all the iteration mappings first
            foreach (SsisObject childobj in Children) {
                if (childobj.DtsObjectType == "DTS:ForEachVariableMapping") {
                    childobj.EmitForEachVariableMapping(indent + "    ", sw);
                }
            }
            sw.WriteLine();

            // Other interior objects and tasks
            EmitChildObjects(indent, sw);

            // Close the loop
            sw.WriteLine(String.Format(@"{0}}}", indent));
        }

        private void EmitForLoop(string indent, StreamWriter sw)
        {
            // Retrieve the three settings from the for loop
            string init = System.Net.WebUtility.HtmlDecode(this.Properties["InitExpression"]).Replace("@","");
            string eval = System.Net.WebUtility.HtmlDecode(this.Properties["EvalExpression"]).Replace("@","");
            string assign = System.Net.WebUtility.HtmlDecode(this.Properties["AssignExpression"]).Replace("@","");
            

            // Write it out
            sw.WriteLine(String.Format(@"{0}for ({1};{2};{3}) {{", indent, init, eval, assign));

            // Inner stuff ?
            EmitChildObjects(indent, sw);

            // Close the loop
            sw.WriteLine(String.Format(@"{0}}}", indent));
        }

        /// <summary>
        /// Write out a function call
        /// </summary>
        /// <param name="indent"></param>
        /// <param name="sw"></param>
        private void EmitFunctionCall(string indent, StreamWriter sw)
        {
            sw.WriteLine(String.Format(@"{0}{1}();", indent, GetFunctionName()));
        }

        /// <summary>
        /// Write out an SQL statement
        /// </summary>
        /// <param name="indent_depth"></param>
        /// <param name="sw"></param>
        private void EmitSqlStatement(string indent, StreamWriter sw)
        {
            // Retrieve the connection string object
            string conn_guid = Attributes["SQLTask:Connection"];
            string connstr = ConnectionWriter.GetConnectionStringName(conn_guid);
            string connprefix = ConnectionWriter.GetConnectionStringPrefix(conn_guid);

            // Retrieve the SQL String and put it in a resource
            string sql_attr_name = ProjectWriter.AddSqlResource(GetParentDtsName(), Attributes["SQLTask:SqlStatementSource"]);

            // Write the using clause for the connection
            if (this.Attributes["SQLTask:ResultType"] == "ResultSetType_SingleRow") {
                sw.WriteLine(@"{0}object result = null;", indent, connstr);
            } else {
                sw.WriteLine(@"{0}DataTable result = null;", indent, connstr);
            }
            sw.WriteLine(@"", indent, connstr);
            sw.WriteLine(@"{0}using (var conn = new {2}Connection(ConfigurationManager.AppSettings[""{1}""])) {{", indent, connstr, connprefix);
            sw.WriteLine(@"{0}    conn.Open();", indent);
            sw.WriteLine(@"{0}    using (var cmd = new {2}Command(Resource1.{1}, conn)) {{", indent, sql_attr_name, connprefix);

            // Handle our parameter binding
            foreach (SsisObject childobj in Children) {
                if (childobj.DtsObjectType == "SQLTask:ParameterBinding") {
                    sw.WriteLine(@"{0}        cmd.Parameters.AddWithValue(""{1}"", {2});", indent, childobj.Attributes["SQLTask:ParameterName"], FixVariableName(childobj.Attributes["SQLTask:DtsVariableName"]));
                }
            }

            // What type of variable reading are we doing?
            if (this.Attributes["SQLTask:ResultType"] == "ResultSetType_SingleRow") {
                sw.WriteLine(@"{0}        result = cmd.ExecuteScalar();", indent);
            } else {
                sw.WriteLine(@"{0}        {1}DataReader dr = cmd.ExecuteReader();", indent, connprefix);
                sw.WriteLine(@"{0}        result = new DataTable();", indent);
                sw.WriteLine(@"{0}        result.Load(dr);", indent);
                sw.WriteLine(@"{0}        dr.Close();", indent);
            }

            // Finish up the SQL call
            sw.WriteLine(@"{0}    }}", indent);
            sw.WriteLine(@"{0}}}", indent);

            // Do we have a result binding?
            SsisObject binding = GetChildByType("SQLTask:ResultBinding");
            if (binding != null) {
                string varname = binding.Attributes["SQLTask:DtsVariableName"];
                string fixedname = FixVariableName(varname);
                VariableData vd = _var_dict[fixedname];

                // Emit our binding
                sw.WriteLine(@"{0}", indent);
                sw.WriteLine(@"{0}// Bind results to {1}", indent, varname);
                if (vd.CSharpType == "DataTable") {
                    sw.WriteLine(@"{0}{1} = result;", indent, FixVariableName(varname));
                } else {
                    sw.WriteLine(@"{0}{1} = ({2})result;", indent, FixVariableName(varname), vd.CSharpType);
                }
            }
        }

        private string GetParentDtsName()
        {
            SsisObject obj = this;
            while (obj != null && obj.DtsObjectName == null) {
                obj = obj.Parent;
            }
            if (obj == null) {
                return "Unnamed";
            } else {
                return obj.DtsObjectName;
            }
        }
        #endregion

        #region Pipeline Logic
        private void EmitPipeline(string indent, StreamWriter sw)
        {
            // Find the component container
            var component_container = GetChildByType("DTS:ObjectData").GetChildByType("pipeline").GetChildByType("components");
            if (component_container == null) {
                HelpWriter.Help(this, "Unable to find SSIS components!");
                return;
            }

            // Produce a "row count" variable we can use
            sw.WriteLine(@"{0}int row_count = 0;", indent);

            // Produce all the readers
            var readers = component_container.GetChildrenByTypeAndAttr("componentClassID", "{BCEFE59B-6819-47F7-A125-63753B33ABB7}");
            foreach (SsisObject child in readers) {

                // Put in a comment for each component
                sw.WriteLine();
                sw.WriteLine(@"{0}// {1}", indent, child.Attributes["name"]);

                // Emit the reader logic
                child.EmitPipelineReader(indent, sw);
                _lineage_columns.AddRange(child._lineage_columns);
            }

            // Iterate through all transformations
            var transforms = component_container.GetChildrenByTypeAndAttr("componentClassID", "{BD06A22E-BC69-4AF7-A69B-C44C2EF684BB}");
            foreach (SsisObject child in transforms) {

                // Put in a comment for each component
                sw.WriteLine();
                sw.WriteLine(@"{0}// {1}", indent, child.Attributes["name"]);

                // Produce the transformation
                child.EmitPipelineTransform(indent, sw);
                _lineage_columns.AddRange(child._lineage_columns);
            }

            // Iterate through all writers
            var writers = component_container.GetChildrenByTypeAndAttr("componentClassID", "{5A0B62E8-D91D-49F5-94A5-7BE58DE508F0}");
            foreach (SsisObject child in writers) {

                // Put in a comment for each component
                sw.WriteLine();
                sw.WriteLine(@"{0}// {1}", indent, child.Attributes["name"]);

                // What type of component is this?  Is it a reader?
                child._lineage_columns = this._lineage_columns;
                child.EmitPipelineWriter(indent, sw);
            }
        }

        private List<SsisObject> GetChildrenByTypeAndAttr(string attr_key, string value)
        {
            List<SsisObject> list = new List<SsisObject>();
            foreach (SsisObject child in Children) {
                string attr = null;
                if (child.Attributes.TryGetValue(attr_key, out attr) && string.Equals(attr, value)) {
                    list.Add(child);
                }
            }
            return list;
        }

        private void EmitPipelineWriter(string indent, StreamWriter sw)
        {
            // Get the connection string GUID: it's this.connections.connection
            string conn_guid = this.GetChildByType("connections").GetChildByType("connection").Attributes["connectionManagerID"];
            string connstr = ConnectionWriter.GetConnectionStringName(conn_guid);
            string connprefix = ConnectionWriter.GetConnectionStringPrefix(conn_guid);

            // It's our problem to produce the SQL statement, because this writer uses calculated data!
            StringBuilder sql = new StringBuilder();
            StringBuilder colnames = new StringBuilder();
            StringBuilder varnames = new StringBuilder();
            StringBuilder paramsetup = new StringBuilder();

            // Retrieve the names of the columns we're inserting
            SsisObject metadata = this.GetChildByType("inputs").GetChildByType("input").GetChildByType("externalMetadataColumns");
            SsisObject columns = this.GetChildByType("inputs").GetChildByType("input").GetChildByType("inputColumns");

            // Okay, let's produce the columns we're inserting
            foreach (SsisObject column in columns.Children) {
                SsisObject mdcol = metadata.GetChildByTypeAndAttr("externalMetadataColumn", "id", column.Attributes["externalMetadataColumnId"]);

                // List of columns in the insert
                colnames.Append(mdcol.Attributes["name"]);
                colnames.Append(", ");

                // List of parameter names in the values clause
                varnames.Append("@");
                varnames.Append(mdcol.Attributes["name"]);
                varnames.Append(", ");

                // Find the source column in our lineage data
                string lineageId = column.Attributes["lineageId"];
                LineageObject lo = (from l in _lineage_columns where l.LineageId == lineageId select l).FirstOrDefault();

                // Parameter setup instructions
                if (lo == null) {
                    HelpWriter.Help(this, "I couldn't find lineage column " + lineageId);
                    paramsetup.AppendFormat(@"{0}            // Unable to find column {1}{2}", indent, lineageId, Environment.NewLine);
                } else {
                    paramsetup.AppendFormat(@"{0}            cmd.Parameters.AddWithValue(""@{1}"",{2}.Rows[row][{3}]);
", indent, mdcol.Attributes["name"], lo.DataTableName, lo.DataTableColumn);
                }
            }
            colnames.Length -= 2;
            varnames.Length -= 2;

            // Produce the SQL statement
            sql.Append("INSERT INTO ");
            sql.Append(GetChildByType("properties").GetChildByTypeAndAttr("property", "name", "OpenRowset").ContentValue);
            sql.Append(" (");
            sql.Append(colnames.ToString());
            sql.Append(") VALUES ");
            sql.Append(varnames.ToString());
            string sql_resource_name = ProjectWriter.AddSqlResource(GetParentDtsName() + "_WritePipe", sql.ToString());

            // Produce a data set that we're going to process - name it after ourselves
            sw.WriteLine(@"{0}DataTable component{1} = new DataTable();", indent, this.Attributes["id"]);

            // Write the using clause for the connection
            sw.WriteLine(@"{0}using (var conn = new {2}Connection(ConfigurationManager.AppSettings[""{1}""])) {{", indent, connstr, connprefix);
            sw.WriteLine(@"{0}    conn.Open();", indent);

            // TODO: SQL Parameters should go in here

            // This is the laziest possible way to do this insert - may want to improve it later
            sw.WriteLine(@"{0}    for (int row = 0; row < row_count; row++) {{", indent);
            sw.WriteLine(@"{0}        using (var cmd = new {2}Command(Resource1.{1}, conn)) {{", indent, sql_resource_name, connprefix);
            sw.WriteLine(paramsetup);
            sw.WriteLine(@"{0}            cmd.ExecuteNonQuery();", indent);
            sw.WriteLine(@"{0}        }}", indent);
            sw.WriteLine(@"{0}    }}", indent);
            sw.WriteLine(@"{0}}}", indent);
        }

        private void EmitPipelineTransform(string indent, StreamWriter sw)
        {
            // Create a new datatable
            sw.WriteLine(@"{0}DataTable component{1} = new DataTable();", indent, this.Attributes["id"]);

            // Add the columns we're generating
            int i = 0;
            foreach (SsisObject outcol in this.GetChildByType("outputs").GetChildByTypeAndAttr("output", "isErrorOut", "false").GetChildByType("outputColumns").Children) {
                LineageObject lo = new LineageObject(outcol, this);
                lo.DataTableColumn = i;
                i++;
                _lineage_columns.Add(lo);

                // Print out this column
                sw.WriteLine(@"{0}component{1}.Columns.Add(new DataColumn(""{2}"", typeof({3})));", indent, this.Attributes["id"], outcol.Attributes["name"], LookupSsisTypeName(outcol.Attributes["dataType"]));
                DataTable dt = new DataTable();
            }

            // Populate these columns
            sw.WriteLine(@"{0}for (int row = 0; row < row_count; row++) {{", indent);
            sw.WriteLine(@"{0}    // TODO: Transform the columns here", indent);
            sw.WriteLine(@"{0}}}", indent);
        }

        private void EmitPipelineReader(string indent, StreamWriter sw)
        {
            // Get the connection string GUID: it's this.connections.connection
            string conn_guid = this.GetChildByType("connections").GetChildByType("connection").Attributes["connectionManagerID"];
            string connstr = ConnectionWriter.GetConnectionStringName(conn_guid);
            string connprefix = ConnectionWriter.GetConnectionStringPrefix(conn_guid);

            // Get the SQL statement
            string sql = this.GetChildByType("properties").GetChildByTypeAndAttr("property", "name", "SqlCommand").ContentValue;
            if (sql == null) sql = "COULD NOT FIND SQL STATEMENT";
            string sql_resource_name = ProjectWriter.AddSqlResource(GetParentDtsName() + "_ReadPipe", sql);

            // Produce a data set that we're going to process - name it after ourselves
            sw.WriteLine(@"{0}DataTable component{1} = new DataTable();", indent, this.Attributes["id"]);

            // Keep track of the lineage of all of our output columns 
            // TODO: Handle error output columns
            int i = 0;
            foreach (SsisObject outcol in this.GetChildByType("outputs").GetChildByTypeAndAttr("output", "isErrorOut", "false").GetChildByType("outputColumns").Children) {
                LineageObject lo = new LineageObject(outcol, this);
                lo.DataTableColumn = i;
                i++;
                _lineage_columns.Add(lo);
            }

            // Write the using clause for the connection
            sw.WriteLine(@"{0}using (var conn = new {2}Connection(ConfigurationManager.AppSettings[""{1}""])) {{", indent, connstr, connprefix);
            sw.WriteLine(@"{0}    conn.Open();", indent);
            sw.WriteLine(@"{0}    using (var cmd = new {2}Command(Resource1.{1}, conn)) {{", indent, sql_resource_name, connprefix);

            // Okay, let's load the parameters
            var paramlist = this.GetChildByType("properties").GetChildByTypeAndAttr("property", "name", "ParameterMapping");
            if (paramlist != null && paramlist.ContentValue != null) {
                string[] p = paramlist.ContentValue.Split(';');
                int paramnum = 0;
                foreach (string oneparam in p) {
                    if (!String.IsNullOrEmpty(oneparam)) {
                        string[] parts = oneparam.Split(',');
                        Guid g = Guid.Parse(parts[1]);

                        // Look up this GUID - can we find it?
                        SsisObject v = GetObjectByGuid(g);
                        if (connprefix == "OleDb") {
                            sw.WriteLine(@"{0}        cmd.Parameters.Add(new OleDbParameter(""@p{2}"",{1}));", indent, v.DtsObjectName, paramnum);
                        } else {
                            sw.WriteLine(@"{0}        cmd.Parameters.AddWithValue(""@{1}"",{2});", indent, parts[0], v.DtsObjectName);
                        }
                    }
                    paramnum++;
                }
            }

            // Finish up the pipeline reader
            sw.WriteLine(@"{0}        {1}DataReader dr = cmd.ExecuteReader();", indent, connprefix);
            sw.WriteLine(@"{0}        component{1}.Load(dr);", indent, this.Attributes["id"]);
            sw.WriteLine(@"{0}        dr.Close();", indent);
            sw.WriteLine(@"{0}    }}", indent);
            sw.WriteLine(@"{0}}}", indent);

            // Set our row count
            sw.WriteLine(@"{0}row_count = component{1}.Rows.Count;", indent, this.Attributes["id"]);
        }
        #endregion

        #region Helper functions
        public static string FixExpression(string expression)
        {
            // Match variables
            Regex rgx = new Regex("[@][[](?<namespace>.*)[:][:](?<var>.*)]");
            string s = rgx.Replace(expression, "$2");

            return s
                .Replace("@", "")
                .Replace("True", "true")
                .Replace("False", "false");
        }

        /// <summary>
        /// Converts the namespace into something usable by C#
        /// </summary>
        /// <param name="original_variable_name"></param>
        /// <returns></returns>
        public static string FixVariableName(string original_variable_name)
        {
            // We are simply stripping out namespaces for the moment
            int p = original_variable_name.IndexOf("::");
            if (p > 0) {
                return original_variable_name.Substring(p + 2);
            }
            return original_variable_name;
        }

        private static List<string> _func_names = new List<string>();
        public string GetFunctionName()
        {
            if (_FunctionName == null) {
                Regex rgx = new Regex("[^a-zA-Z0-9]");
                string fn = rgx.Replace(GetParentDtsName(), "_");

                // Uniqueify!
                int i = 0;
                string newfn = fn;
                while (_func_names.Contains(newfn)) {
                    i++;
                    newfn = fn + "_" + i.ToString();
                }
                _FunctionName = newfn;
                _func_names.Add(_FunctionName);
            }
            return _FunctionName;
        }

        private static string LookupSsisTypeName(string p)
        {
            if (p == "i2") {
                return "System.Int16";
            } else if (p == "str") {
                return "System.String";
            } else {
                HelpWriter.Help(null, "I don't yet understand the SSIS type named " + p);
            }
            return null;
        }

        private static Dictionary<Guid, SsisObject> _guid_lookup = new Dictionary<Guid, SsisObject>();
        public static SsisObject GetObjectByGuid(string s)
        {
            return GetObjectByGuid(Guid.Parse(s));
        }

        public static SsisObject GetObjectByGuid(Guid g)
        {
            var v = _guid_lookup[g];
            if (v == null) {
                HelpWriter.Help(null, "Can't find object matching GUID " + g.ToString());
            }
            return v;
        }

        private static List<string> _folder_names = new List<string>();
        public string GetFolderName()
        {
            if (_FolderName == null) {
                Regex rgx = new Regex("[^a-zA-Z0-9]");
                string fn = rgx.Replace(GetParentDtsName(), "");

                // Uniqueify!
                int i = 0;
                string newfn = fn;
                while (_folder_names.Contains(newfn)) {
                    i++;
                    newfn = fn + "_" + i.ToString();
                }
                _FolderName = newfn;
                _folder_names.Add(_FolderName);
            }
            return _FolderName;
        }

        public Guid GetNearestGuid()
        {
            SsisObject o = this;
            while (o != null && (o.DtsId == null || o.DtsId == Guid.Empty)) {
                o = o.Parent;
            }
            if (o != null) return o.DtsId;
            return Guid.Empty;
        }
        #endregion
    }
}
