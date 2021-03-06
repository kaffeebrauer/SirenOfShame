﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using log4net;
using Microsoft.TeamFoundation.Build.Client;
using Microsoft.TeamFoundation.Framework.Client;
using SirenOfShame.Lib;
using SirenOfShame.Lib.Exceptions;
using SirenOfShame.Lib.Watcher;
using BuildStatus = SirenOfShame.Lib.Watcher.BuildStatus;

namespace TfsServices.Configuration
{
    public class CachedCommentsRetriever
    {
        private struct CommentAndHash
        {
            public CommentAndHash(string newBuildHash, MyChangeset getLatestChangeset)
                : this()
            {
                BuildStatusHash = newBuildHash;
                Changeset = getLatestChangeset;
            }

            public MyChangeset Changeset { get; private set; }
            public string BuildStatusHash { get; private set; }
        }

        private static readonly Dictionary<string, CommentAndHash> CachedCommentsByBuildDefinition = new Dictionary<string, CommentAndHash>();

        public BuildStatus GetCommentsIntoBuildStatus(MyTfsBuildDefinition buildDefinition, BuildStatus buildStatus)
        {
            MyChangeset changeset = GetCommentsForBuild(buildDefinition, buildStatus);
            return AddCommentsToBuildStatus(buildStatus, changeset);
        }

        private static BuildStatus AddCommentsToBuildStatus(BuildStatus buildStatus, MyChangeset changeset)
        {
            if (changeset == null) return null;
            buildStatus.Comment = changeset.Comment;
            buildStatus.BuildId = changeset.ChangesetId.ToString(CultureInfo.InvariantCulture);
            return buildStatus;
        }

        private MyChangeset GetCommentsForBuild(MyTfsBuildDefinition buildDefinition, BuildStatus buildStatus)
        {
            var newBuildHash = buildStatus.GetBuildDataAsHash();
            CommentAndHash cachedChangeset;
            bool haveEverGottenCommentsForThisBuildDef = CachedCommentsByBuildDefinition.TryGetValue(buildDefinition.Name, out cachedChangeset);
            bool areCacheCommentsStale = false;
            if (haveEverGottenCommentsForThisBuildDef)
            {
                string oldBuildHash = cachedChangeset.BuildStatusHash;
                areCacheCommentsStale = oldBuildHash != newBuildHash;
            }
            if (!haveEverGottenCommentsForThisBuildDef || areCacheCommentsStale)
            {
                MyChangeset latestChangeset = buildDefinition.GetLatestChangeset();
                CachedCommentsByBuildDefinition[buildDefinition.Name] = new CommentAndHash(newBuildHash, latestChangeset);
            }
            return CachedCommentsByBuildDefinition[buildDefinition.Name].Changeset;
        }
    }

    public class MyBuildServer
    {
        private static readonly ILog Log = MyLogManager.GetLogger(typeof(MyBuildServer));
        private readonly IBuildServer _buildServer;
        private MyTfsProject _tfsProject;
        private bool _firstRequest = true;
        Dictionary<String, String> _UriToName = new Dictionary<String, String>();

        public MyBuildServer(IBuildServer buildServer, MyTfsProject myTfsProject)
        {
            _buildServer = buildServer;
            _tfsProject = myTfsProject;
        }

        public IEnumerable<BuildStatus> GetBuildStatuses(IEnumerable<MyTfsBuildDefinition> buildDefinitionsQuery, bool applyBuildQuality)
        {
            var buildDefinitionUris = buildDefinitionsQuery.Select(bd => bd.Uri);

            IBuildDetailSpec[] buildDetailSpec = new IBuildDetailSpec[2];

            buildDetailSpec[0] = _buildServer.CreateBuildDetailSpec(buildDefinitionUris);
            buildDetailSpec[0].Status = Microsoft.TeamFoundation.Build.Client.BuildStatus.InProgress;
            buildDetailSpec[0].QueryOrder = BuildQueryOrder.FinishTimeDescending;
            buildDetailSpec[0].InformationTypes = new string[] { "AssociatedChangeset" };
            buildDetailSpec[0].QueryOptions = _firstRequest ? QueryOptions.Process : QueryOptions.None;

            buildDetailSpec[1] = _buildServer.CreateBuildDetailSpec(buildDefinitionUris);
            buildDetailSpec[1].MaxBuildsPerDefinition = 1;
            buildDetailSpec[1].Status = Microsoft.TeamFoundation.Build.Client.BuildStatus.All;
            buildDetailSpec[1].QueryOrder = BuildQueryOrder.FinishTimeDescending;
            buildDetailSpec[1].InformationTypes = new string[] { "AssociatedChangeset" };
            buildDetailSpec[1].QueryOptions = _firstRequest ? QueryOptions.Process : QueryOptions.None;

            _firstRequest = false;

            IBuildQueryResult[] buildQueryResult = _buildServer.QueryBuilds(buildDetailSpec);

            Dictionary<String, BuildStatus> buildStatuses = new Dictionary<String, BuildStatus>();
            Dictionary<String, IBuildDetail> buildDetail = new Dictionary<String, IBuildDetail>();

            // Get last completed for each def
            foreach (var build in buildQueryResult[1].Builds)
                buildDetail[GetBuildDefIdFromBuildDefUri(build.BuildDefinitionUri)] = build;

            // Get current build (if any) and overwrite last completed
            foreach (var build in buildQueryResult[0].Builds)
                buildDetail[GetBuildDefIdFromBuildDefUri(build.BuildDefinitionUri)] = build;

            foreach (var build in buildDetail)
            {
                if (!buildStatuses.ContainsKey(build.Key))
                    buildStatuses[build.Key] = CreateBuildStatus(build.Value, applyBuildQuality);
            }

            return buildStatuses.Values.ToArray();

        }

        private String GetBuildDefIdFromBuildDefUri(Uri uri)
        {
            return uri.Segments[uri.Segments.Length - 1].ToString();
        }
         
        private static BuildStatusEnum GetBuildStatusEnum(Microsoft.TeamFoundation.Build.Client.BuildStatus status)
        {
            switch (status)
            {
                case (Microsoft.TeamFoundation.Build.Client.BuildStatus.Failed):
                    return BuildStatusEnum.Broken;
                case (Microsoft.TeamFoundation.Build.Client.BuildStatus.Succeeded):
                    return BuildStatusEnum.Working;
                case (Microsoft.TeamFoundation.Build.Client.BuildStatus.PartiallySucceeded):
                    return BuildStatusEnum.Broken;
                case (Microsoft.TeamFoundation.Build.Client.BuildStatus.InProgress):
                    return BuildStatusEnum.InProgress;
                case (Microsoft.TeamFoundation.Build.Client.BuildStatus.NotStarted):
                    // it briefly goes to NotStarted after new successes or fails for some reason
                    return BuildStatusEnum.InProgress;
                default:
                    Log.Debug("Unhandeled build status returned from server: " + status);
                    return BuildStatusEnum.Unknown;
            }
        }

        private static BuildStatusEnum GetBuildStatusEnum(String quality)
        {
            switch (quality)
            {
                case "Initial Test Passed":
                case "Lab Test Passed":
                case "Ready for Deployment":
                case "Released":
                case "UAT Passed":
                    return BuildStatusEnum.Working;
                case "Under Investigation":
                    return BuildStatusEnum.InProgress;
                case "Rejected":
                    return BuildStatusEnum.Broken;
                default:
                    return BuildStatusEnum.Unknown;
            }
        }

        private BuildStatus CreateBuildStatus(IBuildDetail buildDetail, bool applyBuildQuality)
        {
            BuildStatusEnum status = BuildStatusEnum.Unknown;
            status = GetBuildStatusEnum(buildDetail.Status);
            if (applyBuildQuality && status == BuildStatusEnum.Working)
                status = GetBuildStatusEnum(buildDetail.Quality);
            
            var result = new BuildStatus
            {
                BuildDefinitionId = buildDetail.BuildDefinitionUri.Segments[buildDetail.BuildDefinitionUri.Segments.Length - 1].ToString(),
                BuildStatusEnum = status,
                RequestedBy = buildDetail.RequestedBy != null ? buildDetail.RequestedBy :
                    buildDetail.RequestedFor != null ? buildDetail.RequestedFor : buildDetail.LastChangedBy,
                StartedTime = buildDetail.StartTime == DateTime.MinValue ? (DateTime?)null : buildDetail.StartTime,
                FinishedTime = buildDetail.FinishTime == DateTime.MinValue ? (DateTime?)null : buildDetail.FinishTime,
            };

            if (buildDetail.BuildDefinition != null)
            {
                this._UriToName[buildDetail.BuildDefinitionUri.ToString()] = buildDetail.BuildDefinition.Name;
            }

            result.Name = this._UriToName[buildDetail.BuildDefinitionUri.ToString()];
            result.BuildId = buildDetail.Uri.Segments[buildDetail.Uri.Segments.Length - 1].ToString();

            var changesets = buildDetail.Information.GetNodesByType("AssociatedChangeset");

            if (changesets.Count() > 0)
            {
                HashSet<String> users = new HashSet<String>();
                foreach (var changeset in changesets)
                    users.Add(changeset.Fields["CheckedInBy"]);

                if (users.Count() > 1)
                    result.RequestedBy = "(Multiple Users)";
                else
                    result.RequestedBy = users.First();

                if (changesets.Count() > 1)
                    result.Comment = "(Multiple Changesets)";
                else
                    result.Comment = changesets.First().Fields["Comment"];
            }
            else 
            {
                result.Comment = buildDetail.Reason.ToString();
            }
            
            if (applyBuildQuality &&
                GetBuildStatusEnum(buildDetail.Quality) == BuildStatusEnum.Broken)
            {
                result.Comment =
                    "Build deployment or test failure. Please see test server or test results for details.\n" +
                    result.Comment;
            }

            result.Url = _tfsProject.ConvertTfsUriToUrl(buildDetail.Uri);

            return result;
        }

        private static BuildStatusEnum GetBuildStatusEnum(IBuildDetail buildDetail, bool applyBuildQuality)
        {
            if (applyBuildQuality)
            {
                var qualityBasedBuildStatus = GetBuildStatusEnum(buildDetail.Quality);
                if (qualityBasedBuildStatus != BuildStatusEnum.Unknown) 
                    return qualityBasedBuildStatus;
            }
            return GetBuildStatusEnum(buildDetail.Status);
        }

        private static Uri GetUriFromBuildServer(IBuildServer buildServer)
        {
            if (buildServer == null) return null;
            if (buildServer.TeamProjectCollection == null) return null;
            if (buildServer.TeamProjectCollection.Uri == null) return null;
            return buildServer.TeamProjectCollection.Uri;
        }

        public override bool Equals(object obj)
        {
            if (obj == null) return false;
            var myUri = BuildServerUri;
            if (myUri == null) return false;

            if (!(obj is MyBuildServer)) return false;
            var theirUri = ((MyBuildServer)obj).BuildServerUri;
            return myUri.Equals(theirUri);
        }

        private Uri BuildServerUri
        {
            get { return GetUriFromBuildServer(_buildServer); }
        }

        public override int GetHashCode()
        {
            var uri = BuildServerUri;
            if (uri == null) return 0;
            return uri.GetHashCode();
        }
    }
}
