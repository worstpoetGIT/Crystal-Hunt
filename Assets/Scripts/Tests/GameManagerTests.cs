using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class GameManagerTests
{
    private GameObject testGameObject;
    private GameManager gameManager;

    [SetUp]
    public void Setup()
    {
        // Create a new GameObject with GameManager
        testGameObject = new GameObject("TestGameManager");
        gameManager = testGameObject.AddComponent<GameManager>();
    }

    [TearDown]
    public void Teardown()
    {
        // Clean up after each test
        Object.DestroyImmediate(testGameObject);
    }

    [Test]
    public void SetupSampleBoundary_ShouldInitializeBoundaryAndLocations()
    {
        gameManager.SetupSampleBoundary();

        Assert.That(gameManager.boundaryPoints.Length, Is.EqualTo(4));

        Assert.That(gameManager.boundaryPoints[0], Is.EqualTo(new Vector2(0, 0)));   // Bottom-left
        Assert.That(gameManager.boundaryPoints[1], Is.EqualTo(new Vector2(10, 0)));  // Bottom-right
        Assert.That(gameManager.boundaryPoints[2], Is.EqualTo(new Vector2(10, 10))); // Top-right
        Assert.That(gameManager.boundaryPoints[3], Is.EqualTo(new Vector2(0, 10)));  // Top-left

        Assert.That(gameManager.locations.Count, Is.EqualTo(1));
        Assert.That(gameManager.locations[0].Length, Is.EqualTo(4));
    }

    [Test]
    public void IsPointInRect_ShouldReturnTrueForInsidePoint()
    {
        gameManager.SetupSampleBoundary();

        Vector2 testPoint = new Vector2(5, 5); // Inside the 0–10 range
        bool result = gameManager.IsPointInRect(testPoint, gameManager.boundaryPoints);

        Assert.IsTrue(result);
    }

    [Test]
    public void IsPointInRect_ShouldReturnFalseForOutsidePoint()
    {
        gameManager.SetupSampleBoundary();

        Vector2 testPoint = new Vector2(15, 15); // Outside the boundary
        bool result = gameManager.IsPointInRect(testPoint, gameManager.boundaryPoints);

        Assert.IsFalse(result);
    }

    [UnityTest]
    public IEnumerator GameManager_ExistsInPlayMode()
    {
        yield return null; // Wait one frame to simulate Play Mode

        var foundGM = Object.FindObjectOfType<GameManager>();
        Assert.IsNotNull(foundGM, "GameManager should exist in Play Mode.");
    }
}
