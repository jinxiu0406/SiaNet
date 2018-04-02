﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CNTK;
using SiaNet.EventArgs;
using SiaNet.Model;
using SiaNet.Model.Optimizers;
using SiaNet.Processing;

namespace SiaNet
{
    public class CompiledModel : IDisposable
    {
        /// <summary>
        ///     Occurs when [on batch end].
        /// </summary>
        public event EventHandler<BatchEndEventArgs> BatchEnd;

        /// <summary>
        ///     Occurs when [on batch start].
        /// </summary>
        public event EventHandler<BatchStartEventArgs> BatchStart;

        /// <summary>
        ///     Occurs when [on epoch end].
        /// </summary>
        public event EventHandler<EpochEndEventArgs> EpochEnd;

        /// <summary>
        ///     Occurs when [on epoch start].
        /// </summary>
        public event EventHandler<EpochStartEventArgs> EpochStart;

        /// <summary>
        ///     Occurs when [on training end].
        /// </summary>
        public event EventHandler<TrainingEndEventArgs> TrainingEnd;

        /// <summary>
        ///     Occurs when [on training start].
        /// </summary>
        public event EventHandler TrainingStart;

        protected readonly Variable FeatureVariable;
        protected readonly Variable LabelVariable;
        protected readonly Function Model;

        internal CompiledModel(Function model)
        {
            Model = model;
            LabelVariable = Variable.InputVariable(new[] {Model.Output.Shape[0]}, DataType.Float);
            FeatureVariable = Model.Inputs.FirstOrDefault(variable => variable.IsInput);
        }

        public Shape InputShape
        {
            get => Shape.FromNDShape(FeatureVariable.Shape);
        }

        public Shape OutputShape
        {
            get => Shape.FromNDShape(LabelVariable.Shape);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            FeatureVariable?.Dispose();
            LabelVariable?.Dispose();
            Model?.Dispose();
        }

        public static CompiledModel Load(string modelFilename)
        {
            return new CompiledModel(Function.Load(modelFilename, GlobalParameters.Device));
        }

        public static CompiledModel Load(byte[] binaryModel)
        {
            return new CompiledModel(Function.Load(binaryModel, GlobalParameters.Device));
        }

        public static CompiledModel Load(Stream modelStream)
        {
            using (var memoryStream = new MemoryStream())
            {
                modelStream.CopyTo(memoryStream);

                return Load(memoryStream.ToArray());
            }
        }

        public double Evaluate(
            XYFrame validation,
            uint batchSize,
            string lossFunctionName,
            string metricFunctionName = null)
        {
            return Evaluate(validation, batchSize, lossFunctionName, out _, metricFunctionName);
        }

        public double Evaluate(
            XYFrame validation,
            uint batchSize,
            string lossFunctionName,
            out double metric,
            string metricFunctionName = null)
        {
            var losses = new List<double>();
            var metrics = new List<double>();

            using (var actualVariable = CNTKLib.InputVariable(LabelVariable.Shape, DataType.Float))
            using (var lossFunction = Losses.Get(lossFunctionName, LabelVariable, actualVariable))
            {
                var metricFunction = !string.IsNullOrEmpty(metricFunctionName)
                    ? Metrics.Get(metricFunctionName, LabelVariable, actualVariable)
                    : null;
                var currentBatch = 1u;
                while (validation.ToBatch(currentBatch, batchSize))
                {
                    using (var actual = Evaluate(validation.CurrentBatch.XFrame))
                    using (var expected = DataFrameUtil.GetValueBatch(validation.CurrentBatch.YFrame))
                    {
                        var inputDataMap =
                            new Dictionary<Variable, Value> {{LabelVariable, expected}, {actualVariable, actual}};
                        var outputDataMap = new Dictionary<Variable, Value> {{lossFunction.Output, null}};

                        lossFunction.Evaluate(inputDataMap, outputDataMap, GlobalParameters.Device);
                        var batchLoss = outputDataMap[lossFunction.Output].GetDenseData<float>(lossFunction.Output).Select(x => x.First()).Average();
                        losses.Add(batchLoss);

                        if (metricFunction != null)
                        {
                            outputDataMap = new Dictionary<Variable, Value> {{metricFunction.Output, null}};

                            metricFunction.Evaluate(inputDataMap, outputDataMap, GlobalParameters.Device);
                            var batchMetric = outputDataMap[metricFunction.Output].GetDenseData<float>(metricFunction.Output).Select(x => x.First()).Average();
                            metrics.Add(batchMetric);
                        }
                    }
                    currentBatch++;
                }
                metricFunction?.Dispose();
            }

            var loss = losses.Average();
            metric = metrics.Any() ? metrics.Average() : loss;
            return loss;
        }

        /// <summary>
        ///     Fits the model for a fixed number of epochs.
        /// </summary>
        /// <param name="train">The training dataset.</param>
        /// <param name="epoches">The no. of trainin epoches.</param>
        /// <param name="batchSize">Size of the batch for training.</param>
        /// <param name="validation">The validation dataset.</param>
        /// <param name="shuffle">Shuffle the dataset while training</param>
        public void Fit(
            XYFrame train,
            uint epoches,
            uint batchSize,
            string optimizerName,
            string lossFunctionName,
            string metricFunctionName = null,
            Regulizers regulizer = null,
            XYFrame validation = null,
            bool shuffle = false)
        {
            var learners = new List<Learner>();
            var optimizerInstance = new BaseOptimizer(optimizerName);
            learners.Add(optimizerInstance.GetDefault(Model, regulizer));

            var lastEpochLoss = 0d;
            var lastEpochMetric = 0d;
            var lastEvaluationLoss = 0d;
            var lastEvaluationMetric = 0d;
            using (var lossFunction = Losses.Get(lossFunctionName, LabelVariable, Model))
            using (var metricFunction = Metrics.Get(!string.IsNullOrWhiteSpace(metricFunctionName) ? metricFunctionName : lossFunctionName, LabelVariable, Model))
            using (var trainer = Trainer.CreateTrainer(Model, lossFunction, metricFunction, learners))
            {
                OnTrainingStart();
                var currentEpoch = 1u;
                while (currentEpoch <= epoches)
                {
                    if (shuffle)
                    {
                        train.Shuffle();
                    }

                    OnEpochStart(currentEpoch);
                    var currentBatch = 1u;
                    var epochLosses = new List<double>();
                    var epochMetrics = new List<double>();
                    while (train.ToBatch(currentBatch, batchSize))
                    {
                        OnBatchStart(currentEpoch, currentBatch);

                        using (var features = DataFrameUtil.GetValueBatch(train.CurrentBatch.XFrame))
                        using (var labels = DataFrameUtil.GetValueBatch(train.CurrentBatch.YFrame))
                        {
                            trainer.TrainMinibatch(
                                new Dictionary<Variable, Value>
                                {
                                    {FeatureVariable, features},
                                    {LabelVariable, labels}
                                }, false,
                                GlobalParameters.Device);
                        }

                        var batchLoss = trainer.PreviousMinibatchLossAverage();
                        var batchMetric = trainer.PreviousMinibatchEvaluationAverage();
                        epochLosses.Add(batchLoss);
                        epochMetrics.Add(batchMetric);

                        OnBatchEnd(currentEpoch, currentBatch, trainer.TotalNumberOfSamplesSeen(), batchLoss,
                            batchMetric);
                        currentBatch++;
                    }

                    lastEpochLoss = epochLosses.Average();
                    lastEpochMetric = epochMetrics.Average();

                    if (validation != null)
                    {
                        lastEvaluationLoss = Evaluate(validation, batchSize, lossFunctionName, out lastEvaluationMetric,
                            metricFunctionName);
                    }

                    OnEpochEnd(currentEpoch, trainer.TotalNumberOfSamplesSeen(), lastEpochLoss, lastEvaluationLoss,
                        lastEpochMetric, lastEvaluationMetric);
                    currentEpoch++;
                }
            }

            foreach (var learner in learners)
            {
                learner.Dispose();
            }
            GC.Collect();

            OnTrainingEnd(lastEpochLoss, lastEvaluationLoss, lastEpochMetric, lastEvaluationMetric);
        }

        /// <summary>
        ///     Predicts the specified data.
        /// </summary>
        /// <param name="data">The data for prediction.</param>
        /// <returns>List of prediction values</returns>
        public float[] Predict(DataFrame data)
        {
            var outputValue = Evaluate(data);
            var resultSet = outputValue.GetDenseData<float>(Model.Output);
            var result = resultSet[0];

            return result.ToArray();
        }

        public void Save(string modelFilename)
        {
            Model.Save(modelFilename);
        }

        public void Save(Stream modelStream)
        {
            var modelBytes = Model.Save();
            modelStream.Write(modelBytes, 0, modelBytes.Length);
        }

        protected Value Evaluate(DataFrame data)
        {
            using (var features = DataFrameUtil.GetValueBatch(data))
            {
                var inputDataMap = new Dictionary<Variable, Value> {{FeatureVariable, features}};
                var outputDataMap = new Dictionary<Variable, Value> {{Model.Output, null}};
                Model.Evaluate(inputDataMap, outputDataMap, GlobalParameters.Device);
                return outputDataMap[Model.Output];
            }
        }

        protected void OnBatchEnd(uint epoch, uint batch, ulong samplesSeen, double loss, double metric)
        {
            BatchEnd?.Invoke(this, new BatchEndEventArgs(epoch, batch, samplesSeen, loss, metric));
        }

        protected void OnBatchStart(uint epoch, uint batch)
        {
            BatchStart?.Invoke(this, new BatchStartEventArgs(epoch, batch));
        }

        protected void OnEpochEnd(
            uint epoch,
            ulong samplesSeen,
            double loss,
            double validationLoss,
            double metric,
            double validationMetric)
        {
            EpochEnd?.Invoke(this,
                new EpochEndEventArgs(epoch, samplesSeen, loss, validationLoss, metric, validationMetric));
        }

        protected void OnEpochStart(uint epoch)
        {
            EpochStart?.Invoke(this, new EpochStartEventArgs(epoch));
        }

        protected void OnTrainingEnd(double loss, double validationLoss, double metric, double validationMetric)
        {
            TrainingEnd?.Invoke(this, new TrainingEndEventArgs(loss, validationLoss, metric, validationMetric));
        }

        protected void OnTrainingStart()
        {
            TrainingStart?.Invoke(this, new System.EventArgs());
        }
    }
}